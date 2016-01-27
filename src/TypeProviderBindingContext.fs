﻿// Copyright 2011-2015, Tomas Petricek (http://tomasp.net), Gustavo Guerra (http://functionalflow.co.uk), and other contributors
// Licensed under the Apache License, Version 2.0, see LICENSE.md in this project
//
// A binding context for cross-targeting type providers

module internal ProviderImplementation.TypeProviderBindingContext

open System
open System.IO
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open ProviderImplementation.AssemblyReader
open ProviderImplementation.AssemblyReaderReflection

type System.Object with 
   member private x.GetProperty(nm) = 
       let ty = x.GetType()
       let prop = ty.GetProperty(nm, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
       let v = prop.GetValue(x,null)
       v
   member private x.GetField(nm) = 
       let ty = x.GetType()
       let fld = ty.GetField(nm, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
       let v = fld.GetValue(x)
       v
   member private x.HasField(nm) = 
       let ty = x.GetType()
       let fld = ty.GetField(nm, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)
       fld <> null
   member private x.GetElements() = [ for v in (x :?> System.Collections.IEnumerable) do yield v ]



/// Represents the type binding context for the type provider based on the set of assemblies
/// referenced by the compilation.
type internal TypeProviderBindingContext(referencedAssemblyPaths : string list) as this = 
    /// Find which assembly defines System.Object etc.
    let systemRuntimeScopeRef = 
      lazy
        referencedAssemblyPaths |> List.tryPick (fun path -> 
          try
            let simpleName = Path.GetFileNameWithoutExtension path
            if simpleName = "mscorlib" || simpleName = "System.Runtime" then 
                let reader = ILModuleReaderAfterReadingAllBytes (path, mkILGlobals EcmaMscorlibScopeRef)
                let mdef = reader.ILModuleDef
                match mdef.TypeDefs.TryFindByName(USome "System", "Object") with 
                | None -> None
                | Some _ -> 
                    let m = mdef.ManifestOfAssembly 
                    let assRef = ILAssemblyRef(m.Name, None, (match m.PublicKey with Some k -> Some (PublicKey.KeyAsToken(k)) | None -> None), m.Retargetable, m.Version, m.Locale)
                    Some (ILScopeRef.Assembly assRef)
            else
                None
          with _ -> None )
        |> function 
           | None -> EcmaMscorlibScopeRef // failwith "no reference to mscorlib.dll or System.Runtime.dll found" 
           | Some r -> r
 
    let fsharpCoreRefVersion = 
      lazy
        referencedAssemblyPaths |> List.tryPick (fun path -> 
          try
            let simpleName = Path.GetFileNameWithoutExtension path
            if simpleName = "FSharp.Core" then 
                let reader = ILModuleReaderAfterReadingAllBytes (path, mkILGlobals EcmaMscorlibScopeRef)
                match reader.ILModuleDef.Manifest with 
                | Some m -> m.Version
                | None -> None
            else
                None
          with _ -> None )
        |> function 
           | None -> typeof<int list>.Assembly.GetName().Version // failwith "no reference to FSharp.Core found" 
           | Some r -> r
 
    let ilGlobals = lazy mkILGlobals (systemRuntimeScopeRef.Force())
    let readers = 
        lazy ([| for ref in referencedAssemblyPaths -> 
                  ref,lazy (try let reader = ILModuleReaderAfterReadingAllBytes(ref, ilGlobals.Force())
                                Choice1Of2(ContextAssembly(ilGlobals.Force(), this.TryBindAssembly, reader, ref)) 
                            with err -> Choice2Of2 err) |])
    let readersTable =  lazy ([| for (ref, asm) in readers.Force() do let simpleName = Path.GetFileNameWithoutExtension ref in yield simpleName, asm |] |> Map.ofArray)
    let referencedAssemblies = lazy ([| for (_,asm) in readers.Force() do match asm.Force() with Choice2Of2 _ -> () | Choice1Of2 asm -> yield asm :> Assembly |])

    let TryBindAssemblySimple(simpleName:string) : Choice<ContextAssembly, exn> = 
        if readersTable.Force().ContainsKey(simpleName) then readersTable.Force().[simpleName].Force() 
        else Choice2Of2 (Exception(sprintf "assembly %s not found" simpleName))

    member __.TryBindAssembly(aref: ILAssemblyRef) : Choice<ContextAssembly, exn> = TryBindAssemblySimple(aref.Name) 
    member __.TryBindAssembly(aref: AssemblyName) : Choice<ContextAssembly, exn> = TryBindAssemblySimple(aref.Name) 
    member __.ReferencedAssemblyPaths = referencedAssemblyPaths
    member __.ReferencedAssemblies =  referencedAssemblies
    member x.TryGetFSharpCoreAssemblyVersion() = fsharpCoreRefVersion.Force()

    static member Create (cfg : TypeProviderConfig) = 

        // Use the reflection hack to determine the set of referenced assemblies by reflecting over the SystemRuntimeContainsType
        // closure in the TypeProviderConfig object.  
        let referencedAssemblies = 
            try 
                let systemRuntimeContainsTypeObj = cfg.GetField("systemRuntimeContainsType") 
                // Account for https://github.com/Microsoft/visualfsharp/pull/591
                let systemRuntimeContainsTypeObj2 = 
                    if systemRuntimeContainsTypeObj.HasField("systemRuntimeContainsTypeRef") then 
                        systemRuntimeContainsTypeObj.GetField("systemRuntimeContainsTypeRef").GetProperty("Value")
                    else
                        systemRuntimeContainsTypeObj
                let tcImportsObj = systemRuntimeContainsTypeObj2.GetField("tcImports")
                [ for dllInfo in tcImportsObj.GetField("dllInfos").GetElements() -> (dllInfo.GetProperty("FileName") :?> string)
                  for dllInfo in tcImportsObj.GetProperty("Base").GetProperty("Value").GetField("dllInfos").GetElements() -> (dllInfo.GetProperty("FileName") :?> string) ]
            with _ ->
                []

        let bindingContext =  TypeProviderBindingContext(referencedAssemblies)

        let designTimeAssemblies = 
          lazy
            [| yield Assembly.GetExecutingAssembly() 
               for asm in Assembly.GetExecutingAssembly().GetReferencedAssemblies() do
                   let asm = try Assembly.Load(asm) with _ -> null
                   if asm <> null then 
                       yield asm |]

        let replacer = AssemblyReplacer (designTimeAssemblies, bindingContext.ReferencedAssemblies)

        bindingContext, replacer


