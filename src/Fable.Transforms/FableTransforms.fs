module Fable.Transforms.FableTransforms

open Fable
open Fable.AST.Fable

let isIdentCaptured identName expr =
    let rec loop isClosure exprs =
        match exprs with
        | [] -> false
        | expr::restExprs ->
            match expr with
            | IdentExpr i when i.Name = identName -> isClosure
            | Lambda(_,body,_) -> loop true [body] || loop isClosure restExprs
            | Delegate(_,body,_,_) -> loop true [body] || loop isClosure restExprs
            | ObjectExpr(members, _, baseCall) ->
                let memberExprs = members |> List.map (fun m -> m.Body)
                loop true memberExprs || loop isClosure (Option.toList baseCall @ restExprs)
            | e ->
                let sub = getSubExpressions e
                loop isClosure (sub @ restExprs)
    loop false [expr]

let isTailRecursive identName expr =
    let mutable isTailRec = true
    let mutable isRecursive = false
    let rec loop inTailPos = function
        | CurriedApply(IdentExpr i, _, _, _)
        | Call(IdentExpr i, _, _, _) as e when i.Name = identName ->
            isRecursive <- true
            isTailRec <- isTailRec && inTailPos
            getSubExpressions e |> List.iter (loop false)
        | Sequential exprs ->
            let lastIndex = (List.length exprs) - 1
            exprs |> List.iteri (fun i e -> loop (i = lastIndex) e)
        | Let(_, value, body) ->
            loop false value
            loop inTailPos body
        | LetRec(bindings, body) ->
            List.map snd bindings |> List.iter (loop false)
            loop inTailPos body
        | IfThenElse(cond, thenExpr, elseExpr, _) ->
            loop false cond
            loop inTailPos thenExpr
            loop inTailPos elseExpr
        | DecisionTree(expr, targets) ->
            loop false expr
            List.map snd targets |> List.iter (loop inTailPos)
        | e ->
            getSubExpressions e |> List.iter (loop false)
    loop true expr
    isTailRec <- isTailRec && isRecursive
    isRecursive, isTailRec

let replaceValues replacements expr =
    if Map.isEmpty replacements
    then expr
    else expr |> visitFromInsideOut (function
        | IdentExpr id as e ->
            match Map.tryFind id.Name replacements with
            | Some e -> e
            | None -> e
        | e -> e)

let replaceValuesAndGenArgs (replacements: Map<string, Expr>) expr =
    if Map.isEmpty replacements then expr
    else
        expr |> visitFromInsideOut (function
            | IdentExpr id as e ->
                match Map.tryFind id.Name replacements with
                | Some e ->
                    if typeEquals true e.Type id.Type then e
                    else
                        extractGenericArgs e id.Type
                        |> replaceGenericArgs e
                | None -> e
            | e -> e)

let replaceNames replacements expr =
    if Map.isEmpty replacements
    then expr
    else expr |> visitFromInsideOut (function
        | IdentExpr id as e ->
            match Map.tryFind id.Name replacements with
            | Some name -> { id with Name=name } |> IdentExpr
            | None -> e
        | e -> e)

let countReferences limit identName body =
    let mutable count = 0
    body |> deepExists (function
        | IdentExpr id2 when id2.Name = identName ->
            count <- count + 1
            count > limit
        | _ -> false) |> ignore
    count

let noSideEffectBeforeIdent identName expr =
    let mutable sideEffect = false
    let orSideEffect found =
        if found then true
        else
            sideEffect <- true
            true

    let rec findIdentOrSideEffect = function
        | Unresolved _ -> false
        | IdentExpr id ->
            if id.Name = identName then true
            elif id.IsMutable then
                sideEffect <- true
                true
            else false
        // If the field is mutable we cannot inline, see #2683
        | Get(e, FieldGet info, _, _) ->
            if info.CanHaveSideEffects then
                sideEffect <- true
                true
            else findIdentOrSideEffect e
        // We don't have enough information here, so just assume there's a side effect just in case
        | Get(_, ExprGet _, _, _) ->
            sideEffect <- true
            true
        | Get(e, (TupleIndex _|UnionField _|UnionTag|ListHead|ListTail|OptionValue _), _, _) ->
            findIdentOrSideEffect e
        | Import _ | Lambda _ | Delegate _ -> false
        | Extended((Throw _|Debugger),_) -> true
        | Extended(Curry(e,_),_) -> findIdentOrSideEffect e
        | CurriedApply(callee, args, _, _) ->
            callee::args |> findIdentOrSideEffectInList |> orSideEffect
        | Call(e1, info, _, _) ->
            match info.Tags, info.Args with
            // HACK: let beta reduction jump over keyValueList/createObj in Fable.React
            | Tags.Contains "pojo", IdentExpr i::_ -> i.Name = identName
            | _ ->
                e1 :: (Option.toList info.ThisArg) @ info.Args
                |> findIdentOrSideEffectInList |> orSideEffect
        | Operation(kind, _, _, _) ->
            match kind with
            | Unary(_, operand) -> findIdentOrSideEffect operand
            | Binary(_, left, right)
            | Logical(_, left, right) -> findIdentOrSideEffect left || findIdentOrSideEffect right
        | Value(value,_) ->
            match value with
            | ThisValue _ | BaseValue _
            | TypeInfo _ | Null _ | UnitConstant | NumberConstant _
            | BoolConstant _ | CharConstant _ | StringConstant _ | RegexConstant _  -> false
            | NewList(None,_) | NewOption(None,_,_) -> false
            | NewOption(Some e,_,_) -> findIdentOrSideEffect e
            | NewList(Some(h,t),_) -> findIdentOrSideEffect h || findIdentOrSideEffect t
            | NewArray(kind,_,_) ->
                match kind with
                | ArrayValues exprs -> findIdentOrSideEffectInList exprs
                | ArrayAlloc e
                | ArrayFrom e -> findIdentOrSideEffect e
            | StringTemplate(_,_,exprs)
            | NewTuple(exprs,_)
            | NewUnion(exprs,_,_,_)
            | NewRecord(exprs,_,_)
            | NewAnonymousRecord(exprs,_,_,_) -> findIdentOrSideEffectInList exprs
        | Sequential exprs -> findIdentOrSideEffectInList exprs
        | Let(_,v,b) -> findIdentOrSideEffect v || findIdentOrSideEffect b
        | TypeCast(e,_)
        | Test(e,_,_) -> findIdentOrSideEffect e
        | IfThenElse(cond, thenExpr, elseExpr,_) ->
            findIdentOrSideEffect cond || findIdentOrSideEffect thenExpr || findIdentOrSideEffect elseExpr
        // TODO: Check member bodies in ObjectExpr
        | ObjectExpr _ | LetRec _ | Emit _ | Set _
        | DecisionTree _ | DecisionTreeSuccess _ // Check sub expressions here?
        | WhileLoop _ | ForLoop _ | TryCatch _ ->
            sideEffect <- true
            true

    and findIdentOrSideEffectInList exprs =
        (false, exprs) ||> List.fold (fun result e ->
            result || findIdentOrSideEffect e)

    findIdentOrSideEffect expr && not sideEffect

let canInlineArg identName value body =
    (canHaveSideEffects value |> not && countReferences 1 identName body <= 1)
     || (noSideEffectBeforeIdent identName body
         && isIdentCaptured identName body |> not
         // Make sure is at least referenced once so the expression is not erased
         && countReferences 1 identName body = 1)

/// Returns arity of lambda (or lambda option) types
let getLambdaTypeArity typ =
    let rec getLambdaTypeArity accArity accArgs = function
        | LambdaType(arg, returnType) ->
            getLambdaTypeArity (accArity + 1) (arg::accArgs) returnType
        | returnType ->
            let argTypes = List.rev accArgs
            let uncurried =
                match typ with
                | Option(_, isStruct) -> Option(DelegateType(argTypes, returnType), isStruct)
                | _ -> DelegateType(argTypes, returnType)
            accArity, uncurried
    match typ with
    | MaybeOption(LambdaType(arg, returnType)) ->
        getLambdaTypeArity 1 [arg] returnType
    | _ -> 0, typ

let tryUncurryType (typ: Type) =
    match getLambdaTypeArity typ with
    | arity, uncurriedType when arity > 1 -> Some(arity, uncurriedType)
    | _ -> None

let uncurryType (typ: Type) =
    match tryUncurryType typ with
    | Some(_arity, uncurriedType) -> uncurriedType
    | None -> typ

module private Transforms =
    let rec (|ImmediatelyApplicable|_|) appliedArgsLen expr =
        if appliedArgsLen = 0 then None
        else
            match expr with
            | Lambda(arg, body, _) ->
                let appliedArgsLen = appliedArgsLen - 1
                if appliedArgsLen = 0 then Some([arg], body)
                else
                    match body with
                    | ImmediatelyApplicable appliedArgsLen (args, body) -> Some(arg::args, body)
                    | _ -> Some([arg], body)
            // If the lambda is immediately applied we don't need the closures
            | NestedRevLets(bindings, Lambda(arg, body, _)) ->
                let body = List.fold (fun body (i,v) -> Let(i, v, body)) body bindings
                let appliedArgsLen = appliedArgsLen - 1
                if appliedArgsLen = 0 then Some([arg], body)
                else
                    match body with
                    | ImmediatelyApplicable appliedArgsLen (args, body) -> Some(arg::args, body)
                    | _ -> Some([arg], body)
            | _ -> None

    let tryInlineBinding (ident: Ident) value letBody =
        let canInlineBinding =
            match value with
            | Import(i,_,_) -> i.IsCompilerGenerated
            // Replace non-recursive lambda bindings
            | NestedLambda(_args, lambdaBody, _name) ->
                match lambdaBody with
                | Import(i,_,_) -> i.IsCompilerGenerated
                // Check the lambda doesn't reference itself recursively
                | _ -> countReferences 0 ident.Name lambdaBody = 0
                    && canInlineArg ident.Name value letBody
            | _ -> canInlineArg ident.Name value letBody

        if canInlineBinding then
            let value =
                match value with
                // Ident becomes the name of the function (mainly used for tail call optimizations)
                | Lambda(arg, funBody, _) -> Lambda(arg, funBody, Some ident.Name)
                | Delegate(args, funBody, _, tags) -> Delegate(args, funBody, Some ident.Name, tags)
                | value -> value
            Some(ident, value)
        else None

    let applyArgs (args: Ident list) (argExprs: Expr list) body =
        let bindings, replacements =
            (([], Map.empty), args, argExprs)
            |||> List.fold2 (fun (bindings, replacements) ident expr ->
                match tryInlineBinding ident expr body with
                | Some(ident, expr) -> bindings, Map.add ident.Name expr replacements
                | None -> (ident, expr)::bindings, replacements)

        let body = replaceValues replacements body
        List.fold (fun body (i, v) -> Let(i, v, body)) body bindings

    let rec lambdaBetaReduction (com: Compiler) e =
        match e with
        | Call(Delegate(args, body, _, _), info, _t, _r) when List.sameLength args info.Args ->
            let body = visitFromOutsideIn (lambdaBetaReduction com) body
            let thisArgExpr = info.ThisArg |> Option.map (visitFromOutsideIn (lambdaBetaReduction com))
            let argExprs = info.Args |> List.map (visitFromOutsideIn (lambdaBetaReduction com))
            let info = { info with ThisArg = thisArgExpr; Args = argExprs }
            applyArgs args info.Args body |> Some

        | NestedApply(applied, argExprs, _t, _r) ->
            let argsLen = List.length argExprs
            match applied with
            | ImmediatelyApplicable argsLen (args, body) when List.sameLength args argExprs ->
                let argExprs = argExprs |> List.map (visitFromOutsideIn (lambdaBetaReduction com))
                let body = visitFromOutsideIn (lambdaBetaReduction com) body
                applyArgs args argExprs body |> Some
            | _ -> None
        | _ -> None

    let bindingBetaReduction (com: Compiler) e =
        // Don't erase user-declared bindings in debug mode for better output
        let isErasingCandidate (ident: Ident) =
            (not com.Options.DebugMode) || ident.IsCompilerGenerated
        match e with
        | Let(ident, value, letBody) when (not ident.IsMutable) && isErasingCandidate ident ->
            match tryInlineBinding ident value letBody with
            | Some(ident, value) ->
                // Sometimes we inline a local generic function, so we need to check
                // if the replaced ident has the concrete type. This happens in FSharp2Fable step,
                // see FSharpExprPatterns.CallWithWitnesses
                replaceValuesAndGenArgs (Map [ident.Name, value]) letBody
            | None -> e
        | e -> e

    let operationReduction (_com: Compiler) e =
        match e with
        // TODO: Other binary operations and numeric types
        | Operation(Binary(AST.BinaryPlus, v1, v2), _, _, _) ->
            match v1, v2 with
            | Value(StringConstant v1, r1), Value(StringConstant v2, r2) ->
                Value(StringConstant(v1 + v2), addRanges [r1; r2])
            // Assume NumberKind and NumberInfo are the same
            | Value(NumberConstant(:? int as v1, AST.Int32, NumberInfo.Empty), r1), Value(NumberConstant(:? int as v2, AST.Int32, NumberInfo.Empty), r2) ->
                Value(NumberConstant(v1 + v2, AST.Int32, NumberInfo.Empty), addRanges [r1; r2])
            | _ -> e

        | Operation(Logical(AST.LogicalAnd, (Value(BoolConstant b, _) as v1), v2), _, _, _) -> if b then v2 else v1
        | Operation(Logical(AST.LogicalAnd, v1, (Value(BoolConstant b, _) as v2)), _, _, _) -> if b then v1 else v2
        | Operation(Logical(AST.LogicalOr, (Value(BoolConstant b, _) as v1), v2), _, _, _) -> if b then v1 else v2
        | Operation(Logical(AST.LogicalOr, v1, (Value(BoolConstant b, _) as v2)), _, _, _) -> if b then v2 else v1

        | IfThenElse(Value(BoolConstant b, _), thenExpr, elseExpr, _) -> if b then thenExpr else elseExpr

        | _ -> e

    let curryIdentsInBody replacements body =
        visitFromInsideOut (function
            | IdentExpr id as e ->
                match Map.tryFind id.Name replacements with
                | Some arity -> Extended(Curry(e, arity), e.Range)
                | None -> e
            | e -> e) body

    let curryArgIdentsAndReplaceInBody (args: Ident list) body =
        let replacements, args =
            ((Map.empty, []), args) ||> List.fold (fun (replacements, uncurriedArgs) arg ->
                match tryUncurryType arg.Type with
                | Some(arity, uncurriedType) ->
                    Map.add arg.Name arity replacements, { arg with Type = uncurriedType}::uncurriedArgs
                | None ->
                    replacements, arg::uncurriedArgs)
        if Map.isEmpty replacements
        then List.rev args, body
        else List.rev args, curryIdentsInBody replacements body

    let uncurryExpr com t arity expr =
        let matches arity arity2 =
            match arity with
            // TODO: check cases where arity <> arity2
            | Some arity -> arity = arity2
            // Remove currying for dynamic operations (no arity)
            | None -> true
        match expr, arity with
        | MaybeCasted(LambdaUncurriedAtCompileTime arity lambda), _ -> lambda
        | Extended(Curry(innerExpr, arity2),_), _
            when matches arity arity2 -> innerExpr
        | Get(Extended(Curry(innerExpr, arity2),_), OptionValue, t, r), _
            when matches arity arity2 -> Get(innerExpr, OptionValue, t, r)
        | Value(NewOption(Some(Extended(Curry(innerExpr, arity2),_)), t, isStruct), r), _
            when matches arity arity2 -> Value(NewOption(Some(innerExpr), t, isStruct), r)
        | _, Some arity -> Replacements.Api.uncurryExprAtRuntime com t arity expr
        | _, None -> expr

    let uncurryArgs com autoUncurrying argTypes args =
        let mapArgs f argTypes args =
            let rec mapArgsInner f acc argTypes args =
                match argTypes, args with
                | head1::tail1, head2::tail2 ->
                    let x = f head1 head2
                    mapArgsInner f (x::acc) tail1 tail2
                | [], head2::tail2 when autoUncurrying ->
                    let x = f Any head2
                    mapArgsInner f (x::acc) [] tail2
                | [], args2 -> (List.rev acc)@args2
                | _, [] -> List.rev acc
            mapArgsInner f [] argTypes args
        (argTypes, args) ||> mapArgs (fun expectedType arg ->
            match expectedType with
            | Any when autoUncurrying -> uncurryExpr com Any None arg
            | _ ->
                match getLambdaTypeArity expectedType with
                | arity, uncurriedType when arity > 1 ->
                    uncurryExpr com uncurriedType (Some arity) arg
                | _ -> arg)

    let uncurryInnerFunctions (_: Compiler) e =
        let curryIdentInBody identName (args: Ident list) body =
            curryIdentsInBody (Map [identName, List.length args]) body
        match e with
        | Let(ident, NestedLambdaWithSameArity(args, fnBody, _), letBody) when List.isMultiple args
                                                                          && not ident.IsMutable ->
            let fnBody = curryIdentInBody ident.Name args fnBody
            let letBody = curryIdentInBody ident.Name args letBody
            Let(ident, Delegate(args, fnBody, None, Tags.empty), letBody)
        // Anonymous lambda immediately applied
        | CurriedApply(NestedLambdaWithSameArity(args, fnBody, Some name), argExprs, t, r)
                        when List.isMultiple args && List.sameLength args argExprs ->
            let fnBody = curryIdentInBody name args fnBody
            let info = makeCallInfo None argExprs (args |> List.map (fun a -> a.Type))
            Delegate(args, fnBody, Some name, Tags.empty)
            |> makeCall r t info
        | e -> e

    let propagateCurryingThroughLets (_: Compiler) = function
        | Let(ident, value, body) when not ident.IsMutable ->
            let ident, value, arity =
                match value with
                | Extended(Curry(innerExpr, arity),_) ->
                    ident, innerExpr, Some arity
                | Get(Extended(Curry(innerExpr, arity),_), OptionValue, t, r) ->
                    ident, Get(innerExpr, OptionValue, t, r), Some arity
                | Value(NewOption(Some(Extended(Curry(innerExpr, arity),_)), t, isStruct), r) ->
                    ident, Value(NewOption(Some(innerExpr), t, isStruct), r), Some arity
                | _ -> ident, value, None
            match arity with
            | None -> Let(ident, value, body)
            | Some arity ->
                let replacements = Map [ident.Name, arity]
                Let({ ident with Type = uncurryType ident.Type }, value, curryIdentsInBody replacements body)
        | e -> e

    let uncurryMemberArgs (m: MemberDecl) =
        let args, body = curryArgIdentsAndReplaceInBody m.Args m.Body
        { m with Args = args; Body = body }

    let (|GetField|_|) (com: Compiler) = function
        | Get(callee, kind, _, r) ->
            match kind with
            | FieldGet { FieldType = Some fieldType } -> Some(callee, fieldType, r)
            | UnionField info ->
                let e = com.GetEntity(info.Entity)
                List.tryItem info.CaseIndex e.UnionCases
                |> Option.bind (fun c -> List.tryItem info.FieldIndex c.UnionCaseFields)
                |> Option.map (fun f -> callee, f.FieldType, r)
            | _ -> None
        | _ -> None

    let curryReceivedArgs (com: Compiler) e =
        match e with
        // Args passed to a lambda are not uncurried, as it's difficult to do it right, see #2657
        // | Lambda(arg, body, name)
        | Delegate(args, body, name, tags) ->
            let args, body = curryArgIdentsAndReplaceInBody args body
            Delegate(args, body, name, tags)
        // Uncurry also values received from getters
        | GetField com (callee, fieldType, r) ->
            match getLambdaTypeArity fieldType, callee.Type with
            // For anonymous records, if the lambda returns a generic the actual
            // arity may be higher than expected, so we need a runtime partial application
            | (arity, MaybeOption(DelegateType(_, GenericParam _))), AnonymousRecordType _ when arity > 0 ->
                let e = Replacements.Api.checkArity com fieldType arity e
                if arity > 1 then Extended(Curry(e, arity), r)
                else e
            | (arity, _), _ when arity > 1 -> Extended(Curry(e, arity), r)
            | _ -> e
        | ObjectExpr(members, t, baseCall) ->
            let members = members |> List.map (fun m ->
                let args, body = curryArgIdentsAndReplaceInBody m.Args m.Body
                { m with Args = args; Body = body })
            ObjectExpr(members, t, baseCall)
        | e -> e

    let uncurrySendingArgs (com: Compiler) e =
        let uncurryConsArgs args (fields: seq<Field>) =
            let argTypes =
                fields
                |> Seq.map (fun fi -> fi.FieldType)
                |> Seq.toList
            uncurryArgs com false argTypes args
        match e with
        | Call(callee, info, t, r) ->
            let args = uncurryArgs com false info.SignatureArgTypes info.Args
            let info = { info with Args = args }
            Call(callee, info, t, r)
        | Emit({ CallInfo = callInfo } as emitInfo, t, r) ->
            let args = uncurryArgs com true callInfo.SignatureArgTypes callInfo.Args
            Emit({ emitInfo with CallInfo = { callInfo with Args = args } }, t, r)
        // Uncurry also values in setters or new record/union/tuple
        | Value(NewRecord(args, ent, genArgs), r) ->
            let args = com.GetEntity(ent).FSharpFields |> uncurryConsArgs args
            Value(NewRecord(args, ent, genArgs), r)
        | Value(NewAnonymousRecord(args, fieldNames, genArgs, isStruct), r) ->
            let args = uncurryArgs com false genArgs args
            Value(NewAnonymousRecord(args, fieldNames, genArgs, isStruct), r)
        | Value(NewUnion(args, tag, ent, genArgs), r) ->
            let uci = com.GetEntity(ent).UnionCases[tag]
            let args = uncurryConsArgs args uci.UnionCaseFields
            Value(NewUnion(args, tag, ent, genArgs), r)
        | Set(e, FieldSet(fieldName), t, value, r) ->
            let value = uncurryArgs com false [t] [value]
            Set(e, FieldSet(fieldName), t, List.head value, r)
        | ObjectExpr(members, t, baseCall) ->
            let members =
                members |> List.map (fun m ->
                    match com.TryGetMember(m.MemberRef) with
                    | Some mRef ->
                        let isGetterOrValueWithoutGenerics =
                            mRef.IsGetter || (mRef.IsValue && List.isEmpty mRef.GenericParameters)
                        if isGetterOrValueWithoutGenerics then
                            let value = uncurryArgs com false [mRef.ReturnParameter.Type] [m.Body]
                            { m with Body = List.head value }
                        else m
                    | None -> m)
            ObjectExpr(members, t, baseCall)
        | e -> e

    let rec uncurryApplications (com: Compiler) e =
        let uncurryApply r t applied args uncurriedArity =
            let argsLen = List.length args
            if uncurriedArity = argsLen then
                // This is already uncurried we don't need the signature arg types anymore,
                // just make a normal call
                let info = makeCallInfo None args []
                makeCall r t info applied |> Some
            elif uncurriedArity < argsLen then
                let appliedArgs, restArgs = List.splitAt uncurriedArity args
                let info = makeCallInfo None appliedArgs []
                let intermediateType =
                    match List.rev restArgs with
                    | [] -> Any
                    | arg::args -> (LambdaType(arg.Type, t), args) ||> List.fold (fun t a -> LambdaType(a.Type, t))
                let applied = makeCall None intermediateType info applied
                CurriedApply(applied, restArgs, t, r) |> Some
            else
                Replacements.Api.partialApplyAtRuntime com t (uncurriedArity - argsLen) applied args |> Some
        match e with
        | NestedApply(applied, args, t, r) ->
            let applied = visitFromOutsideIn (uncurryApplications com) applied
            let args = args |> List.map (visitFromOutsideIn (uncurryApplications com))
            match applied with
            | Extended(Curry(applied, uncurriedArity),_) ->
                uncurryApply r t applied args uncurriedArity
            | Get(Extended(Curry(applied, uncurriedArity),_), OptionValue, t2, r2) ->
                uncurryApply r t (Get(applied, OptionValue, t2, r2)) args uncurriedArity
            | _ -> CurriedApply(applied, args, t, r) |> Some
        | _ -> None

open Transforms

// ATTENTION: Order of transforms matters
let getTransformations (_com: Compiler) =
    [ // First apply beta reduction
      fun com e -> visitFromInsideOut (bindingBetaReduction com) e
      fun com e -> visitFromOutsideIn (lambdaBetaReduction com) e
      fun com e -> visitFromInsideOut (operationReduction com) e
      // Then apply uncurry optimizations
      // Functions passed as arguments in calls (but NOT in curried applications) are being uncurried so we have to re-curry them
      // The next steps will uncurry them again if they're immediately applied or passed again as call arguments
      fun com e -> visitFromInsideOut (curryReceivedArgs com) e
      fun com e -> visitFromInsideOut (uncurryInnerFunctions com) e
      fun com e -> visitFromInsideOut (propagateCurryingThroughLets com) e
      fun com e -> visitFromInsideOut (uncurrySendingArgs com) e
      // uncurryApplications must come after uncurrySendingArgs as it erases argument type info
      fun com e -> visitFromOutsideIn (uncurryApplications com) e
    ]

let rec transformDeclaration transformations (com: Compiler) file decl =
    let transformExpr (com: Compiler) e =
        List.fold (fun e f -> f com e) e transformations

    let transformMemberBody com (m: MemberDecl) =
        { m with Body = transformExpr com m.Body }

    match decl with
    | ModuleDeclaration decl ->
        let members =
            decl.Members
            |> List.map (transformDeclaration transformations com file)
        { decl with Members = members }
        |> ModuleDeclaration

    | ActionDeclaration decl ->
        { decl with Body = transformExpr com decl.Body }
        |> ActionDeclaration

    | MemberDeclaration m ->
        m
        |> uncurryMemberArgs
        |> transformMemberBody com
        |> fun m -> com.ApplyMemberDeclarationPlugin(file, m)
        |> MemberDeclaration

    | ClassDeclaration decl ->
        // (ent, ident, cons, baseCall, attachedMembers)
        let attachedMembers =
            decl.AttachedMembers
            |> List.map (uncurryMemberArgs >> transformMemberBody com)

        let cons, baseCall =
            match decl.Constructor, decl.BaseCall with
            | None, _ -> None, None
            | Some cons, None ->
                uncurryMemberArgs cons |> transformMemberBody com |> Some, None
            | Some cons, Some baseCall ->
                // In order to uncurry correctly the baseCall arguments,
                // we need to include it in the constructor body
                let args, body =
                    Sequential [baseCall; cons.Body]
                    |> curryArgIdentsAndReplaceInBody cons.Args
                transformExpr com body
                |> function
                    | Sequential [baseCall; body] -> Some { cons with Args = args; Body = body }, Some baseCall
                    | body -> Some { cons with Args = args; Body = body }, None // Unexpected, raise error?

        { decl with Constructor = cons
                    BaseCall = baseCall
                    AttachedMembers = attachedMembers }
        |> ClassDeclaration

let transformFile (com: Compiler) (file: File) =
    let transformations = getTransformations com
    let newDecls = List.map (transformDeclaration transformations com file) file.Declarations
    File(newDecls, usedRootNames=file.UsedNamesInRootScope)
