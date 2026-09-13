namespace Procure.Models
{
    /// <summary>How a purchase order's transport is arranged. Stored as text on PurchaseOrder.</summary>
    public static class TransportModes
    {
        /// <summary>One contract for the whole order - the default, and what every order was before v17.</summary>
        public const string Order = "Order";

        /// <summary>Each line carries its own contract, and a line's quantity may split across two.</summary>
        public const string Line = "Line";
    }
}
