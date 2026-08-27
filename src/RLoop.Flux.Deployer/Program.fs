namespace RLoop.Flux.Deployer

open System
open System.IO
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FluxSDK.Build.Incremental
open FluxSDK.Incremental.StepCE
open FluxSDK.Incremental.Store
open FluxSDK.ResoniteLink
open FluxSDK.ResoniteLink.StepExtensions
open ResoniteLink
open ResoniteLink.RPath
open RLoop.Core

type FluxSdkDeployer() =
    interface IFluxDeployer with
        member _.DeployAsync(request: FluxDeployRequest, cancellationToken: CancellationToken) : Task<FluxResult> =
            task {
                let oldOut = Console.Out
                let oldError = Console.Error
                use stdout = new StringWriter()
                use stderr = new StringWriter()
                Console.SetOut(stdout)
                Console.SetError(stderr)
                try
                    try
                        let paths =
                            match Option.ofObj request.LibraryPath with
                            | Some path when not (String.IsNullOrWhiteSpace(path)) -> [| Path.GetFullPath(path) |]
                            | _ -> [||]
                        let store =
                            if paths.Length = 0 then Build.initializeStore()
                            else Build.initializeStoreWith(paths)
                        use link = Link.initialize(request.Url, cancellationToken)
                        store.SetInput(Step.linkInterface, link)
                        let manifest = Build.loadManifest(Path.GetFullPath(request.ProjectDirectory))
                        let config = Build.defaultConfig(manifest)
                        store.SetInput(BuildConfigKey(), config)

                        let parentSlot : Step<Slot> =
                            step {
                                let! slots = Query.findSlotByID(request.ParentSlotId) |> Query.first
                                match slots |> Seq.tryHead with
                                | Some slot -> return slot
                                | None -> return failwith $"Parent slot '{request.ParentSlotId}' was not found."
                            }

                        let inputMap : IReadOnlyDictionary<string, string> =
                            match Option.ofObj request.InputMap with
                            | Some mappings -> mappings
                            | None -> Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
                        let outputMap : IReadOnlyDictionary<string, string> =
                            match Option.ofObj request.OutputMap with
                            | Some mappings -> mappings
                            | None -> Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
                        let target : Loader.DeployTarget =
                            { ParentSlot = parentSlot
                              InputMap = inputMap
                              OutputMap = outputMap }
                        let! result = Step.runStepAsync (Loader.replace config request.Module target) store
                        match result with
                        | Ok slotId ->
                            return FluxResult(true, 0, stdout.ToString(), stderr.ToString(), slotId)
                        | Error message ->
                            stderr.WriteLine(message)
                            return FluxResult(false, 1, stdout.ToString(), stderr.ToString(), null)
                    with ex ->
                        stderr.WriteLine(ex.ToString())
                        return FluxResult(false, 1, stdout.ToString(), stderr.ToString(), null)
                finally
                    Console.SetOut(oldOut)
                    Console.SetError(oldError)
            }
