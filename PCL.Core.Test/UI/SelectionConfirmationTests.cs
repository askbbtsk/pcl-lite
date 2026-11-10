using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class SelectionConfirmationTests
{
    [TestMethod]
    public void DefaultCheckedMigrationRowsAllowImmediateConfirmationWithoutAnyChangeEvent()
    {
        var selected = SelectionConfirmation.SelectedIndices(Enumerable.Repeat(true, 80));
        Assert.IsTrue(selected.Count > 0);
        CollectionAssert.AreEqual(Enumerable.Range(0, 80).ToArray(), selected.ToArray());
    }

    [TestMethod]
    public void PartialInitialSelectionKeepsOriginalRowIndices()
        => CollectionAssert.AreEqual(new[] { 1, 3, 4 },
            SelectionConfirmation.SelectedIndices([false, true, false, true, true]).ToArray());

    [TestMethod]
    public void EmptyOrAllUncheckedListsCannotConfirm()
    {
        Assert.AreEqual(0, SelectionConfirmation.SelectedIndices([]).Count);
        Assert.AreEqual(0, SelectionConfirmation.SelectedIndices([false, false]).Count);
    }

    [TestMethod]
    public void DeselectingAndReselectingUpdatesStateWithoutChangingTheSubmittedIndex()
    {
        var states = new[] { true, true };
        Assert.AreEqual(2, SelectionConfirmation.SelectedIndices(states).Count);
        states[0] = false;
        CollectionAssert.AreEqual(new[] { 1 }, SelectionConfirmation.SelectedIndices(states).ToArray());
        states[1] = false;
        Assert.AreEqual(0, SelectionConfirmation.SelectedIndices(states).Count);
        states[0] = true;
        CollectionAssert.AreEqual(new[] { 0 }, SelectionConfirmation.SelectedIndices(states).ToArray());
    }

    [TestMethod]
    public void CapturedSubmissionDoesNotChangeWhenControlsChangeLater()
    {
        var states = new[] { true, false, true };
        var captured = SelectionConfirmation.SelectedIndices(states);
        Array.Fill(states, false);
        CollectionAssert.AreEqual(new[] { 0, 2 }, captured.ToArray());
    }

    [TestMethod]
    public void NullSelectionIsRejected()
        => Assert.Throws<ArgumentNullException>(() => SelectionConfirmation.SelectedIndices(null!));
}
