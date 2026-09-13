using System;

namespace Procure.Models
{
    // One PR / RFQ / PO attached to a TodoTask. A task can carry several. Label is denormalised
    // for display and re-resolved on load (TodoPageModel.ResolveLinkLabel) so a renamed target
    // still reads correctly.
    public sealed class TaskLink
    {
        public required string EntityType { get; init; }   // "PR" | "RFQ" | "PO"
        public required Guid EntityId { get; init; }
        public string Label { get; set; } = string.Empty;
    }
}
