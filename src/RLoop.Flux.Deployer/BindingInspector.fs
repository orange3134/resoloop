namespace RLoop.Flux.Deployer

open System
open System.Collections.Generic
open RLoop.Core

/// Reads the named I/O layout emitted by the pinned Flux-SDK 1.9.0.
/// Public source: e674229f, ResolveMain.fs and PackResoniteLink.fs (makeHelper/nodeToMessages).
/// Unknown, incomplete or ambiguous layouts are not evidence for a no-op.
type FluxSdkBindingInspector private () =
    static member Verify(moduleSlot: SlotInfo, inputs: IReadOnlyDictionary<string, string>, outputs: IReadOnlyDictionary<string, string>) : bool =
        let single (items: seq<'T>) =
            match items |> Seq.truncate 2 |> Seq.toList with
            | [ item ] -> Some item
            | _ -> None

        let reference (summary: ComponentSummary) name =
            match summary.Members with
            | null -> None
            | members ->
                match members.TryGetValue name with
                | true, memberValue when memberValue.Kind = "reference" && not (String.IsNullOrWhiteSpace memberValue.TargetId) ->
                    Some memberValue.TargetId
                | _ -> None

        let hasType prefix (summary: ComponentSummary) =
            summary.Type.StartsWith(prefix, StringComparison.Ordinal)
            && summary.Type.EndsWith(">", StringComparison.Ordinal)

        let sourcePrefix = "[ProtoFluxBindings]FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes."
        let source name target =
            let names = [ "Input:" + name; "Input:[Elem]" + name; "Input:[Global]" + name ]
            match moduleSlot.Children |> Seq.filter (fun slot -> List.contains slot.Name names) |> single with
            | None -> false
            | Some slot when slot.IsReferenceOnly -> false
            | Some slot ->
                match slot.Components |> Seq.filter (hasType "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<") |> single with
                | None -> false
                | Some backing when reference backing "Reference" <> Some target -> false
                | Some backing ->
                    if slot.Name = "Input:[Global]" + name then true
                    else
                        let kinds =
                            if slot.Name = "Input:[Elem]" + name then [ "ElementSource<" ]
                            else [ "ValueSource<"; "ObjectValueSource<"; "ReferenceSource<" ]
                        match slot.Components |> Seq.filter (fun c -> kinds |> List.exists (fun kind -> hasType (sourcePrefix + kind) c)) |> single with
                        | Some node -> reference node "Source" = Some backing.Id
                        | None -> false

        let drive name target =
            match moduleSlot.Children |> Seq.filter (fun slot -> slot.Name = "Output:" + name) |> single with
            | None -> false
            | Some slot when slot.IsReferenceOnly -> false
            | Some slot ->
                let proxy (c: ComponentSummary) =
                    (c.Type.StartsWith("[FrooxEngine]FrooxEngine.ProtoFlux.CoreNodes.FieldDriveBase<", StringComparison.Ordinal)
                     || c.Type.StartsWith("[FrooxEngine]FrooxEngine.ProtoFlux.CoreNodes.ReferenceDrive<", StringComparison.Ordinal))
                    && c.Type.EndsWith(">+Proxy", StringComparison.Ordinal)
                match slot.Components |> Seq.filter proxy |> single with
                | Some backing when reference backing "Drive" = Some target ->
                    let kinds = [ "ValueFieldDrive<"; "ObjectFieldDrive<"; "ReferenceDrive<" ]
                    match slot.Components |> Seq.filter (fun c -> kinds |> List.exists (fun kind -> hasType (sourcePrefix + kind) c)) |> single with
                    | Some node -> reference backing "Node" = Some node.Id
                    | None -> false
                | _ -> false

        not moduleSlot.IsReferenceOnly
        && (inputs |> Seq.forall (fun pair -> not (String.IsNullOrWhiteSpace pair.Value) && source pair.Key pair.Value))
        && (outputs |> Seq.forall (fun pair -> not (String.IsNullOrWhiteSpace pair.Value) && drive pair.Key pair.Value))
