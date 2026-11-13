using System;
using System.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;

namespace PCL;

public partial class FormMain
{
    private readonly LunarCredentialConsent _lunarCredentialConsent = new();

    private bool EnsureLunarCredentialConsent(PageStackData target)
    {
        if (target.page != PageType.Lunar) return true;
        var accepted = _lunarCredentialConsent.EnsureAccepted(
            () => Config.Preference.Lunar.CredentialRiskAcknowledged,
            seconds => TimedConfirmation.Show(
                Lang.Text("Lunar.CredentialRisk.Message"),
                Lang.Text("Lunar.CredentialRisk.Title"),
                Lang.Text("Lunar.CredentialRisk.Confirm"), seconds: seconds) == 1,
            () => Config.Preference.Lunar.CredentialRiskAcknowledged = true);
        if (accepted) return true;

        // The clicked title radio can already be selected. Cancel must restore
        // the current navigation without constructing/connecting the Lunar page.
        var previous = isChangingPage;
        isChangingPage = true;
        try
        {
            foreach (var button in PanTitleSelect.Children.OfType<MyRadioButton>())
                if (button.Tag is not null &&
                    double.TryParse(button.Tag.ToString(), out var tag))
                    button.SetChecked(tag == (double)pageCurrent.page, false, false);
        }
        finally { isChangingPage = previous; }
        return false;
    }
}
