using System;
using System.Collections.Generic;

namespace PCL.Core.UI;

/// <summary>The same selected indices drive both initial confirmation state and submitted results.</summary>
public static class SelectionConfirmation
{
    public static List<int> SelectedIndices(IEnumerable<bool> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var indices = new List<int>();
        var index = 0;
        foreach (var selected in selections)
        {
            if (selected) indices.Add(index);
            index++;
        }
        return indices;
    }
}
