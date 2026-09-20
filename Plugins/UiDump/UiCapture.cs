namespace UiDump;

using System.Diagnostics;

// No game dependencies: traversal and completeness are tested with synthetic trees.
internal sealed class UiNode
{
    public string Path { get; set; } = "";
    public string? ParentPath { get; set; }
    public string Address { get; set; } = "";
    public string ParentAddress { get; set; } = "";
    public string Vtable { get; set; } = "";
    public uint Flags { get; set; }
    public bool LocalVisible { get; set; }
    public bool EffectiveVisible { get; set; }
    public float?[] LocalPosition { get; set; } = [];
    public float?[] LocalSize { get; set; } = [];
    public float?[]? ScreenRect { get; set; }
    public Dictionary<string, string> TextCandidates { get; set; } = new();
    public string? ItemAddress { get; set; }
    public string? ItemPath { get; set; }
    public string? RawHex { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ChildrenOmitted { get; set; }
    public int DeclaredChildren { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public long[] Children { get; set; } = [];
}

internal sealed class UiSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public string Label { get; set; } = "";
    public int GameProcessId { get; set; }
    public string AreaHash { get; set; } = "";
    public string GameState { get; set; } = "";
    public string RootAddress { get; set; } = "";
    public string Build { get; set; } = "";
    public string WindowRectangle { get; set; } = "";
    public bool IncludeHidden { get; set; }
    public bool IncludeRaw { get; set; }
    public bool Complete { get; set; }
    public string Status { get; set; } = "Capturing";
    public int PendingNodes { get; set; }
    public int DuplicateLinks { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<UiNode> Nodes { get; set; } = new();
}

internal sealed class UiCapture
{
    private readonly Queue<(long Address, string Path, string? Parent, int Depth, bool Visible)> pending = new();
    private readonly HashSet<long> visited = new();
    private readonly int maxNodes;
    private readonly int maxDepth;
    public UiSnapshot Snapshot { get; }
    public bool Done { get; private set; }

    public UiCapture(long root, UiSnapshot snapshot, int maxNodes = 20000, int maxDepth = 64)
    {
        this.Snapshot = snapshot;
        this.maxNodes = maxNodes;
        this.maxDepth = maxDepth;
        this.pending.Enqueue((root, "root", null, 0, true));
    }

    public void Step(Func<long, UiNode?> read, int milliseconds = 4)
    {
        var timer = Stopwatch.StartNew();
        while (!this.Done && this.pending.Count > 0 && timer.ElapsedMilliseconds < milliseconds)
        {
            if (this.visited.Count >= this.maxNodes)
            {
                this.Abort("Node limit reached");
                return;
            }
            var current = this.pending.Dequeue();
            if (current.Address == 0) continue;
            if (!this.visited.Add(current.Address))
            {
                this.Snapshot.DuplicateLinks++;
                this.Snapshot.Issues.Add($"{current.Path}: repeated address 0x{current.Address:X}; link not traversed twice");
                continue;
            }
            UiNode? node;
            try { node = read(current.Address); }
            catch (Exception error)
            {
                node = new UiNode();
                node.Errors.Add(error.GetType().Name + ": " + error.Message);
            }
            if (node == null)
            {
                node = new UiNode();
                node.Errors.Add("UI header unreadable or invalid");
            }
            node.Path = current.Path;
            node.ParentPath = current.Parent;
            node.Address = $"0x{current.Address:X}";
            node.EffectiveVisible = current.Visible && node.LocalVisible;
            this.Snapshot.Nodes.Add(node);
            if (node.Errors.Count > 0)
                this.Snapshot.Issues.Add(current.Path + ": " + string.Join("; ", node.Errors));
            if (!node.EffectiveVisible && !this.Snapshot.IncludeHidden && node.Errors.Count == 0)
            {
                node.ChildrenOmitted = "Hidden subtree excluded by capture scope";
                continue;
            }
            if (current.Depth >= this.maxDepth && node.Children.Length > 0)
            {
                node.ChildrenOmitted = "Depth limit reached";
                this.Snapshot.Issues.Add(current.Path + ": " + node.ChildrenOmitted);
                continue;
            }
            if (this.pending.Count + node.Children.Length > this.maxNodes * 2)
            {
                node.ChildrenOmitted = "Queue limit reached";
                this.Snapshot.Issues.Add(current.Path + ": " + node.ChildrenOmitted);
                continue;
            }
            for (int i = 0; i < node.Children.Length; i++)
                this.pending.Enqueue((node.Children[i], current.Path + "." + i, current.Path, current.Depth + 1, node.EffectiveVisible));
        }
        if (!this.Done && this.pending.Count == 0) this.Finish();
    }

    public void Abort(string reason)
    {
        this.Snapshot.Issues.Add(reason);
        this.Finish();
    }

    private void Finish()
    {
        this.Done = true;
        this.Snapshot.PendingNodes = this.pending.Count;
        this.Snapshot.Complete = this.Snapshot.Issues.Count == 0 && this.pending.Count == 0;
        this.Snapshot.Status = this.Snapshot.Complete ? "Complete within selected scope" : "Partial; inspect Issues";
        this.Snapshot.FinishedUtc = DateTime.UtcNow;
    }
}
