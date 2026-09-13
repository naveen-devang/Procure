using System;

namespace Procure.Models
{
    // A PR / RFQ / PO a task can be attached to. Label is what the picker and the row chip show
    // (e.g. "PR-0042 — bearing housings"); ChipLabel is the short form for the row ("PR-0042").
    public sealed record TaskLinkTarget(string Type, Guid Id, string Label)
    {
        public string ChipLabel
        {
            get
            {
                var dash = Label.IndexOf('—');
                return dash > 0 ? Label[..dash].Trim() : Label;
            }
        }
    }
}
