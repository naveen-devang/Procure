using System;

namespace Procure.Models
{
    // One PR / RFQ / PO attached to a Note. Same shape as TaskLink (kept separate so notes and
    // tasks don't share a table or a type name that reads wrong in either place).
    public sealed class NoteLink
    {
        public required string EntityType { get; init; }   // "PR" | "RFQ" | "PO"
        public required Guid EntityId { get; init; }
        public string Label { get; set; } = string.Empty;
    }
}
