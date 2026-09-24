namespace AgenticRAG.Models;

/// <summary>
/// Shared mutable state for the knowledge base.
/// Registered as a singleton so document state persists across workflow runs.
/// </summary>
public class KnowledgeBaseState
{
    private readonly object _lock = new();
    private bool   _hasSource;
    private string _description = "";

    public bool HasSource
    {
        get { lock (_lock) return _hasSource; }
        set { lock (_lock) _hasSource = value; }
    }

    public string Description
    {
        get { lock (_lock) return _description; }
        set { lock (_lock) _description = value; }
    }

    public void AppendSource(string name)
    {
        lock (_lock)
        {
            _hasSource   = true;
            _description = string.IsNullOrEmpty(_description)
                ? name
                : _description + ", " + name;
        }
    }
}
