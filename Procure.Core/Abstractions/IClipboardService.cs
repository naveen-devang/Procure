using System.Threading.Tasks;

namespace Procure.Abstractions;

/// <summary>System clipboard, text only. Replaces <c>Clipboard.Default</c>.</summary>
public interface IClipboardService
{
    Task<bool> HasTextAsync();
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}
