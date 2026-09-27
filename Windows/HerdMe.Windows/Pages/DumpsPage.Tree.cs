using HerdMe.Windows.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

// The Tree view of a dump. Children are created when a node expands, so a large dump costs
// nothing until it is browsed.
public sealed partial class DumpsPage
{
    private const int ExpandAllLimit = 1_500;
    private static string dumpView = "tree";
    private int treeGeneration;

    private void DumpViewTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        dumpView = sender.SelectedItem?.Tag as string ?? "tree";
        ApplyDumpView();
    }

    private void ApplyDumpView()
    {
        var tree = dumpView == "tree";
        var tab = tree ? DumpTreeTab : DumpTextTab;
        if (!ReferenceEquals(DumpViewTabs.SelectedItem, tab)) DumpViewTabs.SelectedItem = tab;
        var hasTree = DumpTreeView.RootNodes.Count > 0;
        DumpTreeView.Visibility = tree && hasTree ? Visibility.Visible : Visibility.Collapsed;
        DumpTreeEmpty.Visibility = tree && !hasTree && displayedDump is not null ? Visibility.Visible : Visibility.Collapsed;
        DumpTreeTools.Visibility = tree && hasTree ? Visibility.Visible : Visibility.Collapsed;
        SummaryScroll.Visibility = tree ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ShowDumpTree(CapturedDump? dump)
    {
        var generation = ++treeGeneration;
        DumpTreeView.RootNodes.Clear();
        ApplyDumpView();
        if (dump is null || dump.IsSummary || dump.Payload.Length == 0) return;
        var root = await Task.Run(() => DumpTree.FromPayload(dump.Payload));
        if (!loaded || generation != treeGeneration || !ReferenceEquals(displayedDumpForTree, dump)) return;
        if (root is not null)
        {
            // The top-level value is almost always one array or object; show its entries directly.
            var top = root.Children.Count > 0 ? root.Children : new[] { root };
            foreach (var node in top) DumpTreeView.RootNodes.Add(CreateTreeNode(node));
            if (DumpTreeView.RootNodes.Count == 1) DumpTreeView.RootNodes[0].IsExpanded = true;
        }
        ApplyDumpView();
    }

    private CapturedDump? displayedDumpForTree;

    private static TreeViewNode CreateTreeNode(DumpTreeNode node) => new()
    {
        Content = node,
        HasUnrealizedChildren = node.Children.Count > 0
    };

    private static void Realize(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren || node.Content is not DumpTreeNode model) return;
        foreach (var child in model.Children) node.Children.Add(CreateTreeNode(child));
        node.HasUnrealizedChildren = false;
    }

    private void DumpTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args) => Realize(args.Node);

    private void DumpExpandAll_Click(object sender, RoutedEventArgs e)
    {
        var budget = ExpandAllLimit;
        var queue = new Queue<TreeViewNode>(DumpTreeView.RootNodes);
        while (queue.Count > 0 && budget-- > 0)
        {
            var node = queue.Dequeue();
            Realize(node);
            if (node.Children.Count == 0) continue;
            node.IsExpanded = true;
            foreach (var child in node.Children) queue.Enqueue(child);
        }
        if (queue.Count > 0) App.MainWindow.ShowToast(Services.AppLocalization.Get("DumpExpandAllLimited"));
    }

    private void DumpCollapseAll_Click(object sender, RoutedEventArgs e)
    {
        var stack = new Stack<TreeViewNode>(DumpTreeView.RootNodes);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node.IsExpanded = false;
            foreach (var child in node.Children) stack.Push(child);
        }
    }
}
