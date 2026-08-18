using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace NetworkChangePlaybackAddin;

internal sealed class Module1 : Module
{
    private static Module1? _this;
    internal static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("NetworkChangePlaybackAddin_Module");
}
