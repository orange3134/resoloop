using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed partial class ExistingContentService
{
    private sealed record SlotWrite(string Key, string Attribute, MemberValue Before, JsonElement Desired,
        SlotUpdateRequest Request, bool NoOp);

    private static void ValidateSlotEdits(ApplyDocument document, ExistingSnapshot snapshot)
    {
        foreach (var edit in document.Existing!.SlotChanges ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>())
        {
            if (!snapshot.Slots.TryGetValue(edit.Key, out var slot) || slot.Id == "Root")
                throw Error("IMPORT_SLOT_KEY_NOT_FOUND", $"'{edit.Key}' is not an owned content Slot.");
            if (edit.Value is null) throw Error("IMPORT_DOCUMENT_INVALID", "slotChanges must contain attribute objects.");
            foreach (var attribute in edit.Value)
            {
                _ = SlotRequest(slot.Id, attribute.Key, attribute.Value);
                _ = SlotMember(slot, attribute.Key);
            }
        }
    }

    private async Task<IReadOnlyList<SlotWrite>> PrepareSlotWritesAsync(ApplyDocument document, ExistingSnapshot snapshot,
        ExistingEditState state, CancellationToken ct)
    {
        var result = new List<SlotWrite>();
        foreach (var edit in document.Existing!.SlotChanges ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>())
        {
            if (edit.Value.Count == 0) continue;
            var slot = await ReadOwnedSlotAsync(snapshot, edit.Key, ct);
            foreach (var attribute in edit.Value)
            {
                var original = SlotMember(snapshot.Slots[edit.Key], attribute.Key);
                var baseline = state.SlotBaselines.GetValueOrDefault(edit.Key)?.GetValueOrDefault(attribute.Key) ?? original;
                var actual = SlotMember(slot, attribute.Key);
                if (!SameSlotMember(actual, original)) throw Error("IMPORT_BINDING_UNVERIFIED", "Slot member identity/type changed.");
                var noOp = SlotMatches(attribute.Key, actual, attribute.Value);
                if (!noOp && !SameValue(actual, baseline))
                    throw new RLoopException("IMPORT_SLOT_CONFLICT", $"'{edit.Key}.{attribute.Key}' changed in the world; no writes were sent.", ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["baseline"] = baseline.Value, ["current"] = actual.Value, ["desired"] = attribute.Value });
                result.Add(new(edit.Key, attribute.Key, actual, attribute.Value, SlotRequest(slot.Id, attribute.Key, attribute.Value), noOp));
            }
        }
        return result;
    }

    private async Task ApplySlotWritesAsync(Prepared prepared, CancellationToken ct)
    {
        foreach (var write in prepared.SlotWrites)
        {
            var current = await ReadOwnedSlotAsync(prepared.Snapshot, write.Key, ct);
            var actual = SlotMember(current, write.Attribute);
            if (!SameValue(actual, write.Before)) throw Error("IMPORT_SLOT_CONFLICT", $"'{write.Key}.{write.Attribute}' changed after preflight; no further writes were sent.");
            if (!write.NoOp)
            {
                // UpdateSlot omits every unrequested attribute. Never send the captured Slot back wholesale.
                await client.UpdateSlotAsync(write.Request, ct);
                var after = await ReadOwnedSlotAsync(prepared.Snapshot, write.Key, ct);
                var result = SlotMember(after, write.Attribute);
                if (!SameSlotMember(result, actual) || !SlotMatches(write.Attribute, result, write.Desired))
                    throw Error("IMPORT_SLOT_NOT_CONVERGED", $"'{write.Key}.{write.Attribute}' did not retain the requested value; inspect before retrying.");
                actual = result;
            }
            if (!prepared.State.SlotBaselines.TryGetValue(write.Key, out var fields))
                prepared.State.SlotBaselines[write.Key] = fields = new(StringComparer.Ordinal);
            fields[write.Attribute] = actual;
            CheckpointFiles.Write(prepared.StatePath, JsonSerializer.Serialize(prepared.State, Json));
        }
    }

    private static MemberValue SlotMember(SlotInfo slot, string attribute) =>
        slot.Members?.TryGetValue(attribute, out var member) == true && member.Kind == "field" && !string.IsNullOrEmpty(member.Id) && (member.Value is not null || attribute == "Tag")
            ? member : throw Error("IMPORT_SLOT_MEMBER_UNAVAILABLE", $"'{attribute}' needs a freshly observed typed Slot field with an instance ID. Re-import if the old snapshot lacks it.");
    private static bool SameSlotMember(MemberValue a, MemberValue b) => a.Kind == b.Kind && a.Id == b.Id && a.Type == b.Type;

    private static SlotUpdateRequest SlotRequest(string id, string attribute, JsonElement value)
    {
        try
        {
            switch (attribute)
            {
                case "Position": return new(id, Position: Vector3Value.Parse(value.GetRawText(), attribute));
                case "Scale": return new(id, Scale: Vector3Value.Parse(value.GetRawText(), attribute));
                case "Rotation":
                    var q = QuaternionValue.Parse(value.GetRawText(), attribute);
                    var length = (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
                    if (Math.Abs(length - 1) > 0.00001) throw Error("IMPORT_SLOT_VALUE_INVALID", "Rotation requires a normalized finite quaternion [x,y,z,w].");
                    return new(id, Rotation: q);
                case "IsActive": return new(id, IsActive: value.GetBoolean());
                case "IsPersistent": return new(id, IsPersistent: value.GetBoolean());
                case "Tag" when value.ValueKind == JsonValueKind.String: return new(id, Tag: value.GetString());
                case "OrderOffset": return new(id, OrderOffset: value.GetInt64());
                default: throw Error("IMPORT_SLOT_ATTRIBUTE_UNSUPPORTED", "Supported Slot attributes: Position, Rotation, Scale, IsActive, IsPersistent, Tag, OrderOffset. Name/Parent are not managed by slotChanges.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        { throw Error("IMPORT_SLOT_VALUE_INVALID", $"Invalid {attribute} value: {ex.Message}"); }
    }

    private static bool SlotMatches(string attribute, MemberValue actual, JsonElement desired)
    {
        var wanted = SlotRequest("", attribute, desired);
        // Resonite exposes an unset string Tag as null. Empty text is the explicit clear operation.
        if (attribute == "Tag") return (actual.Value?.GetValue<string>() ?? "") == wanted.Tag;
        var current = SlotRequest("", attribute, JsonSerializer.SerializeToElement(actual.Value));
        // The runtime normalizes floating transforms; compare at single-precision accuracy.
        static bool Near(float a, float b) => Math.Abs(a - b) <= 0.000001f * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        static bool Vector(Vector3Value a, Vector3Value b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
        return attribute switch
        {
            "Position" => Vector(current.Position!, wanted.Position!),
            "Scale" => Vector(current.Scale!, wanted.Scale!),
            "Rotation" => Near(current.Rotation!.X, wanted.Rotation!.X) && Near(current.Rotation.Y, wanted.Rotation.Y) &&
                Near(current.Rotation.Z, wanted.Rotation.Z) && Near(current.Rotation.W, wanted.Rotation.W),
            "IsActive" => current.IsActive == wanted.IsActive,
            "IsPersistent" => current.IsPersistent == wanted.IsPersistent,
            "Tag" => current.Tag == wanted.Tag,
            "OrderOffset" => current.OrderOffset == wanted.OrderOffset,
            _ => false
        };
    }
}
