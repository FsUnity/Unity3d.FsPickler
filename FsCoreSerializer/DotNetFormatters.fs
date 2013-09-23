﻿module internal FsCoreSerializer.DotNetFormatters

    open System
    open System.IO
    open System.Reflection
    open System.Threading
#if EMIT_IL
    open System.Linq.Expressions
#endif
    open System.Runtime.Serialization

    open FsCoreSerializer
    open FsCoreSerializer.Utils
    open FsCoreSerializer.FormatterUtils
    open FsCoreSerializer.BaseFormatters

    // formatter rules for array types

    type ArrayFormatter =

        static member Create<'T, 'Array when 'Array :> Array> (resolver : IFormatterResolver) =
            assert(typeof<'T> = typeof<'Array>.GetElementType())
            let ef = resolver.Resolve<'T> ()
            let rank = typeof<'Array>.GetArrayRank()

            let writer (w : Writer) (x : 'Array) =

                for d = 0 to rank - 1 do
                    w.BW.Write(x.GetLength d)

                if ef.TypeInfo = TypeInfo.Primitive then
                    Stream.WriteArray(w.BW.BaseStream, x)
                else
                    let isValue = ef.TypeInfo <= TypeInfo.Value
                             
                    match rank with
                    | 1 ->
                        let x = x :> obj :?> 'T []
                        for i = 0 to x.Length - 1 do
                            write isValue w ef x.[i]
                    | 2 -> 
                        let x = x :> obj :?> 'T [,]
                        for i = 0 to x.GetLength(0) - 1 do
                            for j = 0 to x.GetLength(1) - 1 do
                                write isValue w ef x.[i,j]
                    | 3 ->
                        let x = x :> obj :?> 'T [,,]
                        for i = 0 to x.GetLength(0) - 1 do
                            for j = 0 to x.GetLength(1) - 1 do
                                for k = 0 to x.GetLength(2) - 1 do
                                    write isValue w ef x.[i,j,k]
                    | 4 ->
                        let x = x :> obj :?> 'T [,,,]
                        for i = 0 to x.GetLength(0) - 1 do
                            for j = 0 to x.GetLength(1) - 1 do
                                for k = 0 to x.GetLength(2) - 1 do
                                    for l = 0 to x.GetLength(3) - 1 do
                                        write isValue w ef x.[i,j,k,l]
                    | _ -> failwith "impossible array rank"

            let reader (r : Reader) =
                let l = Array.zeroCreate<int> rank
                for i = 0 to rank - 1 do l.[i] <- r.BR.ReadInt32()

                if ef.TypeInfo = TypeInfo.Primitive then
                    let array =
                        match rank with
                        | 1 -> Array.zeroCreate<'T> l.[0] :> Array
                        | 2 -> Array2D.zeroCreate<'T> l.[0] l.[1] :> Array
                        | 3 -> Array3D.zeroCreate<'T> l.[0] l.[1] l.[2] :> Array
                        | 4 -> Array4D.zeroCreate<'T> l.[0] l.[1] l.[2] l.[3] :> Array
                        | _ -> failwith "impossible array rank"

                    r.EarlyRegisterObject array

                    Stream.CopyToArray(r.BR.BaseStream, array)

                    array :?> 'Array
                else
                    let isValue = ef.TypeInfo <= TypeInfo.Value

                    match rank with
                    | 1 -> 
                        let arr = Array.zeroCreate<'T> l.[0]
                        r.EarlyRegisterObject arr
                        for i = 0 to l.[0] - 1 do
                            arr.[i] <- read isValue r ef
                        arr :> obj :?> 'Array
                    | 2 -> 
                        let arr = Array2D.zeroCreate<'T> l.[0] l.[1]
                        r.EarlyRegisterObject arr
                        for i = 0 to l.[0] - 1 do
                            for j = 0 to l.[1] - 1 do
                                arr.[i,j] <- read isValue r ef
                        arr :> obj :?> 'Array
                    | 3 ->
                        let arr = Array3D.zeroCreate<'T> l.[0] l.[1] l.[2]
                        r.EarlyRegisterObject arr
                        for i = 0 to l.[0] - 1 do
                            for j = 0 to l.[1] - 1 do
                                for k = 0 to l.[2] - 1 do
                                    arr.[i,j,k] <- read isValue r ef
                        arr :> obj :?> 'Array
                    | 4 ->
                        let arr = Array4D.zeroCreate<'T> l.[0] l.[1] l.[2] l.[3]
                        r.EarlyRegisterObject arr
                        for i = 0 to l.[0] - 1 do
                            for j = 0 to l.[1] - 1 do
                                for k = 0 to l.[2] - 1 do
                                    for l = 0 to l.[3] - 1 do
                                        arr.[i,j,k,l] <- read isValue r ef
                        arr :> obj :?> 'Array
                    | _ -> failwith "impossible array rank"

            new Formatter<'Array>(reader, writer, FormatterInfo.ReflectionDerived, true, false)

        static member CreateUntyped(t : Type, resolver : IFormatterResolver) =
            let et = t.GetElementType()
            let m =
                typeof<ArrayFormatter>
                    .GetMethod("Create", BindingFlags.NonPublic ||| BindingFlags.Static)
                    .MakeGenericMethod [| et ; t |]

            try m.Invoke(null, [| resolver :> obj |]) :?> Formatter
            with :? TargetInvocationException as e -> raise e.InnerException

    // formatter builder for ISerializable types

    type ISerializableFormatter =
        static member TryCreate<'T when 'T :> ISerializable>(resolver : IFormatterResolver) =
            match typeof<'T>.TryGetConstructor [| typeof<SerializationInfo> ; typeof<StreamingContext> |] with
            | None -> None
            | Some ctorInfo ->
                let allMethods = typeof<'T>.GetMethods(memberBindings)
                let onSerializing = allMethods |> getSerializationMethods< OnSerializingAttribute>
                let onSerialized = allMethods |> getSerializationMethods<OnSerializedAttribute>
//                let onDeserializing = allMethods |> getSerializationMethods<OnDeserializingAttribute>
                let onDeserialized = allMethods |> getSerializationMethods<OnDeserializedAttribute>

                let isDeserializationCallback = typeof<IDeserializationCallback>.IsAssignableFrom typeof<'T>
#if EMIT_IL
                let inline runW (dele : Action<StreamingContext, 'T> option) (w : Writer) x =
                    match dele with
                    | None -> ()
                    | Some d -> d.Invoke(w.StreamingContext, x)

                let inline runR (dele : Action<StreamingContext, 'T> option) (r : Reader) x =
                    match dele with
                    | None -> ()
                    | Some d -> d.Invoke(r.StreamingContext, x)

                let onSerializing = Expression.preComputeSerializationMethods<'T> onSerializing
                let onSerialized = Expression.preComputeSerializationMethods<'T> onSerialized
                let onDeserialized = Expression.preComputeSerializationMethods<'T> onDeserialized

                let ctor =
                    Expression.compileFunc2<SerializationInfo, StreamingContext, 'T>(fun si sc -> 
                        Expression.New(ctorInfo, si, sc) :> _)

                let inline create si sc = ctor.Invoke(si, sc)
#else
                let inline run (ms : MethodInfo []) o (sc : StreamingContext)  =
                    for i = 0 to ms.Length - 1 do ms.[i].Invoke(o, [| sc :> obj |]) |> ignore

                let inline create (si : SerializationInfo) (sc : StreamingContext) = 
                    ctorInfo.Invoke [| si :> obj ; sc :> obj |]
#endif
                let writer (w : Writer) (x : 'T) =
                    runW onSerializing w x
                    let sI = new SerializationInfo(typeof<'T>, new FormatterConverter())
                    x.GetObjectData(sI, w.StreamingContext)
                    w.BW.Write sI.MemberCount
                    let enum = sI.GetEnumerator()
                    while enum.MoveNext() do
                        w.BW.Write enum.Current.Name
                        w.Write<obj> enum.Current.Value

                    runW onSerialized w x

                let reader (r : Reader) =
                    let sI = new SerializationInfo(typeof<'T>, new FormatterConverter())
                    let memberCount = r.BR.ReadInt32()
                    for i = 1 to memberCount do
                        let name = r.BR.ReadString()
                        let v = r.Read<obj> ()
                        sI.AddValue(name, v)

                    let x = create sI r.StreamingContext

                    runR onDeserialized r x
                    if isDeserializationCallback then (x :> obj :?> IDeserializationCallback).OnDeserialization null
                    x

                let fmt = new Formatter<'T>(reader, writer, FormatterInfo.ISerializable, true, false)
                Some(fmt :> Formatter)

        static member TryCreateUntyped(t : Type, resolver : IFormatterResolver) =
            let m =
                typeof<ISerializableFormatter>
                    .GetMethod("TryCreate", BindingFlags.NonPublic ||| BindingFlags.Static)
                    .MakeGenericMethod [| t |]

            try m.Invoke(null, [| resolver :> obj |]) :?> Formatter option
            with :? TargetInvocationException as e -> raise e.InnerException


    // formatter builder for IFsCoreSerializable types

    type IFsCoreSerialibleFormatter =
        static member Create<'T when 'T :> IFsCoreSerializable>(resolver : IFormatterResolver) =
            match typeof<'T>.TryGetConstructor [| typeof<Reader> |] with
            | None ->
                let msg = "declared IFsCoreSerializable but missing constructor Reader -> T."
                raise <| new NonSerializableTypeException(typeof<'T>, msg)
            | Some ctorInfo ->
#if EMIT_IL
                let reader = Expression.compileFunc1<Reader, 'T>(fun reader -> Expression.New(ctorInfo, reader) :>_).Invoke
#else
                let reader (r : Reader) = ctorInfo.Invoke [| r :> obj |] :?> 'T
#endif
                let writer (w : Writer) (x : 'T) = x.GetObjectData(w)

                new Formatter<'T>(reader, writer, FormatterInfo.IFsCoreSerializable, true, false)

        static member CreateUntyped(t : Type, resolver : IFormatterResolver) =
            let m =
                typeof<IFsCoreSerialibleFormatter>
                    .GetMethod("Create", BindingFlags.NonPublic ||| BindingFlags.Static)
                    .MakeGenericMethod [| t |]

            try m.Invoke(null, [| resolver :> obj |]) :?> Formatter
            with :? TargetInvocationException as e -> raise e.InnerException