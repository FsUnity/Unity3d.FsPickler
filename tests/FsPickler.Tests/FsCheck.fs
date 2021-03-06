﻿namespace Nessos.FsPickler.Tests

open FsCheck

type FsPicklerGenerators private () =
    static let types = 
        [| 
            typeof<int> ; typeof<string> ; typeof<bool> ; typeof<byte> ; typeof<uint32> ; typeof<bigint> ;
            typeof<unit> ; typeof<obj> ; typeof<int list> ; typeof<byte []> ; typeof<System.DateTime> ; typeof<System.Guid>
            typedefof<_ option> ; typedefof<_ list> ; typedefof<Choice<_,_,_>> ; typedefof<_ * _> ; typedefof<_ ref>
            typedefof<Choice<_,_>> ; typedefof<_ * _ * _>
        |]

    static member BigInt = Arb.generate<byte []> |> Gen.map (fun bs -> System.Numerics.BigInteger(bs)) |> Arb.fromGen
    static member Array2D<'T> () = Arb.generate<'T> |> Gen.array2DOf |> Arb.fromGen
    static member Type =
        let rec genTy () = gen {
            let! i = Arb.generate<int>
            let ty = types.[abs i % types.Length]
            let! gty = gen {
                if ty.IsGenericTypeDefinition then
                    let gas = ty.GetGenericArguments()
                    for i = 0 to gas.Length - 1 do
                        let! ga = genTy()
                        gas.[i] <- ga

                    return ty.MakeGenericType gas
                else
                    return ty
            }

            let! makeArray = Arb.generate<int>
            if makeArray % 13 = 0 then
                let! sd = Arb.generate<int>
                let rk = abs sd % 3
                return 
                    if rk = 0 then gty.MakeArrayType()
                    else gty.MakeArrayType(rk + 1)
            else
                return gty
        }

        Arb.fromGen (genTy ())

    static member Array3D<'T> () =
        let mkSized (n : int) =
            gen {
                // sqrt is good enough here
                let chooseSqrtOfSize = Gen.choose(0, n |> float |> sqrt |> int)
                let! N = chooseSqrtOfSize
                let! M = chooseSqrtOfSize
                let! K = chooseSqrtOfSize
                let g = Arb.generate<'T>
                let! seed = Gen.arrayOfLength (N*M*K) g
                return Array3D.init N M K (fun n m k -> seed.[n + m * N + k * N * M])
            }

        mkSized |> Gen.sized |> Arb.fromGen

    static member Array4D<'T> () =
        let mkSized (n : int) =
            gen {
                let chooseSqrtOfSize = Gen.choose(0, n |> float |> sqrt |> sqrt |> int)
                let! N = chooseSqrtOfSize
                let! M = chooseSqrtOfSize
                let! K = chooseSqrtOfSize
                let! L = chooseSqrtOfSize
                let g = Arb.generate<'T>
                let! seed = Gen.arrayOfLength (N*M*K*L) g
                return Array4D.init N M K L (fun n m k l -> seed.[n + m * N + k * N * M + l * N * M * K])
            }

        mkSized |> Gen.sized |> Arb.fromGen

    static member Seq<'T> () = Arb.generate<'T []> |> Gen.map Array.toSeq |> Arb.fromGen

            
type Check =
    /// quick check methods with explicit type annotation
    static member QuickThrowOnFail<'T> (f : 'T -> unit, ?maxRuns) = 
        match maxRuns with
        | None -> Check.QuickThrowOnFailure f
        | Some mxrs -> Check.One({ Config.QuickThrowOnFailure with MaxTest = mxrs }, f)

    /// quick check methods with explicit type annotation
    static member QuickThrowOnFail<'T> (f : 'T -> bool, ?maxRuns) = 
        match maxRuns with
        | None -> Check.QuickThrowOnFailure f
        | Some mxrs -> Check.One({ Config.QuickThrowOnFailure with MaxTest = mxrs }, f)