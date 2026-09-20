using UiDump;

static UiNode Node(bool visible = true, params long[] children) => new()
{
    LocalVisible = visible, Children = children, DeclaredChildren = children.Length,
};
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Run(UiCapture capture, Func<long, UiNode?> read)
{
    for (int i = 0; !capture.Done && i < 100; i++) capture.Step(read, 100);
    Check(capture.Done, "Capture must terminate");
}

var normal = new UiCapture(1, new UiSnapshot());
Run(normal, id => id == 1 ? Node(true, 2, 3) : Node());
Check(normal.Snapshot.Complete && normal.Snapshot.Nodes.Count == 3, "Normal tree must be complete");
Check(normal.Snapshot.Nodes[2].Path == "root.1" && normal.Snapshot.Nodes[2].ParentPath == "root", "Preserve child indices and parent path");

var hidden = new UiCapture(1, new UiSnapshot());
Run(hidden, id => id switch { 1 => Node(true, 2), 2 => Node(false, 3), _ => throw new Exception("Do not read hidden descendants") });
Check(hidden.Snapshot.Complete && hidden.Snapshot.Nodes.Count == 2, "Visible scope omits hidden descendants without pretending failure");
Check(hidden.Snapshot.Nodes[1].ChildrenOmitted != null, "Hidden omission is explicit");

var all = new UiCapture(1, new UiSnapshot { IncludeHidden = true });
Run(all, id => id switch { 1 => Node(true, 2), 2 => Node(false, 3), _ => Node() });
Check(all.Snapshot.Nodes.Count == 3 && !all.Snapshot.Nodes[2].EffectiveVisible, "Hidden ancestor propagates visibility");

var cycle = new UiCapture(1, new UiSnapshot());
Run(cycle, _ => Node(true, 1));
Check(!cycle.Snapshot.Complete && cycle.Snapshot.DuplicateLinks == 1, "Cycles terminate with explicit link evidence");

var missing = new UiCapture(1, new UiSnapshot());
Run(missing, _ => null);
Check(!missing.Snapshot.Complete && missing.Snapshot.Nodes[0].Errors.Count > 0, "Unreadable root cannot be success");

var exception = new UiCapture(1, new UiSnapshot());
Run(exception, _ => throw new IOException("Simulated stale UI"));
Check(!exception.Snapshot.Complete, "Read errors cannot be success");

var limited = new UiCapture(1, new UiSnapshot(), maxNodes: 2);
Run(limited, id => id == 1 ? Node(true, 2, 3) : Node());
Check(!limited.Snapshot.Complete && limited.Snapshot.PendingNodes == 1, "Node limit reports pending work");

var deep = new UiCapture(1, new UiSnapshot(), maxDepth: 1);
Run(deep, id => Node(true, id + 1));
Check(!deep.Snapshot.Complete && deep.Snapshot.Nodes[1].ChildrenOmitted == "Depth limit reached", "Depth limit is not silent");

var queue = new UiCapture(1, new UiSnapshot(), maxNodes: 2);
Run(queue, _ => Node(true, 2, 3, 4, 5, 6));
Check(!queue.Snapshot.Complete && queue.Snapshot.Nodes.Count == 1, "Queue growth is bounded");

var cancelled = new UiCapture(1, new UiSnapshot());
cancelled.Abort("Cancelled");
Check(cancelled.Done && !cancelled.Snapshot.Complete && cancelled.Snapshot.PendingNodes == 1, "Cancel preserves partial evidence");
Console.WriteLine("UiDump: 10 traversal/completeness scenarios passed.");
