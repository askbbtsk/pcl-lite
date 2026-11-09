using System.Windows.Input;

namespace PCL;

public partial class PageInstanceInstall
{
    // The card and the ordinary Modify action deliberately share confirmations and execution.
    private void BeginInstanceMigration_Click(object sender, MouseButtonEventArgs args)
        => BtnSelectStart_Click(sender, args);
}
