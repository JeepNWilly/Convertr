// Copyright (C) 2026 JeepNWilly
// Convertr is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3
// of the License, or (at your option) any later version. It comes with no warranty.
// See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.

using System.ComponentModel;

namespace Convertr;

/// <summary>A BindingList that supports sorting, so DataGridView column headers sort by the real (numeric) values.</summary>
public sealed class SortableBindingList<T> : BindingList<T>
{
    private bool _sorted;
    private PropertyDescriptor? _prop;
    private ListSortDirection _dir;

    public SortableBindingList(IList<T> items) : base(new List<T>(items)) { }

    protected override bool SupportsSortingCore => true;
    protected override bool IsSortedCore => _sorted;
    protected override PropertyDescriptor? SortPropertyCore => _prop;
    protected override ListSortDirection SortDirectionCore => _dir;

    protected override void ApplySortCore(PropertyDescriptor prop, ListSortDirection direction)
    {
        var list = (List<T>)Items;
        var cmp = new ValueComparer();
        var sorted = (direction == ListSortDirection.Ascending
            ? list.OrderBy(x => prop.GetValue(x), cmp)
            : list.OrderByDescending(x => prop.GetValue(x), cmp)).ToList();
        list.Clear();
        list.AddRange(sorted);
        _prop = prop;
        _dir = direction;
        _sorted = true;
        OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
    }

    protected override void RemoveSortCore()
    {
        _sorted = false;
        _prop = null;
    }

    private sealed class ValueComparer : IComparer<object?>
    {
        public int Compare(object? a, object? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            if (a is string sa && b is string sb) return StringComparer.OrdinalIgnoreCase.Compare(sa, sb);
            if (a is IComparable ca && a.GetType() == b.GetType()) return ca.CompareTo(b);
            return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
