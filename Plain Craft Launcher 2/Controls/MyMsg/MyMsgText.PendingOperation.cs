using System.Windows.Controls;

namespace PCL;

public partial class MyMsgText
{
    internal bool UsesConverter(ModMain.MyMsgBoxConverter converter) => ReferenceEquals(myConverter, converter);
    internal void SetWorkMessage(string message) => LabCaption.Text = message;
}
