using System;
using System.Threading.Tasks;

namespace Procure.Pages
{
    public interface IThemeTransitionable
    {
        Task AnimateThemeTransitionAsync(Action applyTheme, bool isGoingToDark);
    }
}
