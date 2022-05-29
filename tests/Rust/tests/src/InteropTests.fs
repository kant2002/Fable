module Fable.Tests.InteropTests

#if FABLE_COMPILER
module Subs =
    open Fable.Core
    [<Emit("$0 + $1")>]
    let add (a, b) = jsNative

    [<Emit("$0 * $1")>]
    let mul a b = jsNative

    [<Emit("{ let mut v = std::vec::Vec::new(); v.append(&mut vec![$0,$1]); Rc::from(MutCell::from(v)) }")>]
    let fixedVec a b = jsNative

    //doesn't currently work, but would be preferred
    // [<Erase, Emit("std::vec::Vec::new()")>]
    // type Vec() =
    //     [<Emit("$0.push($1)")>]
    //     member x.Push a = jsNative

    module Vec =
        [<Emit("Rc<MutCell<Vec<$0>>>")>]
        type VecT<'a> =
            [<Emit("$0.get_mut().push($1)")>]
            abstract Push: 'a -> unit
        [<Emit("Rc::from(MutCell::from(std::vec::Vec::new()))")>]
        let create (): VecT<'a> = jsNative
        [<Emit("$1.get_mut().push($0)")>]
        let push (item: 'a) (vec: VecT<'a>) = jsNative
        [<Emit("{ $1.get_mut().append(&mut vec![$0]); $1 }")>]
        let append (item: 'a) (vec: VecT<'a>): VecT<'a> = jsNative

        [<Emit("$0.len()")>]
        let len (vec: VecT<'a>): nativeint = jsNative

        module FnExps =
            let push42 (v: VecT<_>) = 
                v.Push 42
                v

    module Float =
        [<Emit("$0.sin()")>]
        let sin (x: float): float = jsNative

open Util.Testing

[<Fact>]
let ``simple add sub works`` () =
    let res = Subs.add (2, 3)
    res |> equal 5

[<Fact>]
let ``simple mul sub works`` () =
    let res = Subs.mul 3 2
    res |> equal 6

[<Fact>]
let ``simple float op sin works`` () =
    let res = Subs.Float.sin (0.0)
    res |> equal 0.0

[<Fact>]
let ``fixed vec should work`` () =
    let a = Subs.fixedVec 3 4
    let b = Subs.Vec.create()
    b |> Subs.Vec.push 3
    b |> Subs.Vec.push 4
    a |> equal b


[<Fact>]
let ``vec mutable push should work`` () =
    let a = Subs.Vec.create()
    let b = Subs.Vec.create()
    a |> Subs.Vec.push 1
    b |> Subs.Vec.push 1
    a |> equal b

[<Fact>]
let ``vec mutable append expressed as returnable should work`` () =
    let a = Subs.Vec.create() |> Subs.Vec.append 1 |> Subs.Vec.append 2 |> Subs.Vec.append 3
    let b = Subs.Vec.create() |> Subs.Vec.append 1 |> Subs.Vec.append 2 |> Subs.Vec.append 3
    a |> equal b

[<Fact>]
let ``vec instance mutable push should work`` () =
    let a = Subs.Vec.create()
    let b = Subs.Vec.create()
    a.Push 2
    b.Push 2
    a |> equal b

[<Fact>]
let ``vec instance type emit should work`` () =
    let a = Subs.Vec.create()
    a.Push 42

[<Fact>]
let ``vec instance pass over boundary should work`` () =
    let a = Subs.Vec.create()
    let res = Subs.Vec.FnExps.push42 a
    res |> Subs.Vec.len |> int |> equal 1
#endif