module Fable.Core.Rust

open System

// Force pass by reference
type ByRefAttribute() =
    inherit Attribute()

// Outer attributes
type OuterAttrAttribute private (name: string, value: string option, items: string[]) =
    inherit Attribute()
    new (name: string) = OuterAttrAttribute(name, None, [||])
    new (name: string, value: string) = OuterAttrAttribute(name, Some value, [||])
    new (name: string, items: string[]) = OuterAttrAttribute(name, None, items)

// Inner attributes
type InnerAttrAttribute private (name: string, value: string option, items: string[]) =
    inherit Attribute()
    new (name: string) = InnerAttrAttribute(name, None, [||])
    new (name: string, value: string) = InnerAttrAttribute(name, Some value, [||])
    new (name: string, items: string[]) = InnerAttrAttribute(name, None, items)

//Rc/Arc control
type PointerType =
    | Lrc = 0
    | Rc = 1
    | Arc = 2
    | Box = 3

// Rust - Defines the pointer type that is to be used to wrap the object (Rc/Arc)
type ReferenceTypeAttribute(pointerType: PointerType) =
    inherit Attribute()

/// Destructure a tuple of arguments and apply them to literal code as with EmitAttribute.
/// E.g. `emitExpr (arg1, arg2) "$0 + $1"` becomes `arg1 + arg2`
let emitExpr<'T> (args: obj) (code: string): 'T = nativeOnly

/// Works like `ImportAttribute` (same semantics as Dart imports).
/// You can use "*" selector.
let import<'T> (selector: string) (path: string): 'T = nativeOnly

/// Imports a whole external module.
let importAll<'T> (path: string): 'T = nativeOnly
