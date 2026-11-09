using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Minecraft.Profile;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft;
using PCL.Core.Plugins;
using PCL.Core.UI;
using PCL.Core.UI.Navigation;
using PCL.Core.UI.Theme;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Plugin.SDK;

namespace PCL;

public partial class FormMain
{

    private void FormMain_MouseMove(object sender, MouseEventArgs e)
    {
        lastMouseArg = e;
    }

}
