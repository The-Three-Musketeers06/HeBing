using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

public enum HmiAlignment { Left, HorizontalCenter, Right, Top, VerticalCenter, Bottom }

/// <summary>One document edit, including a drag consisting of many intermediate updates.</summary>
public sealed class HmiEditTransaction : IDisposable
{
    private HmiEditorSession _session;
    internal HmiEditTransaction(HmiEditorSession session) => _session = session;
    public void Commit() { var session = _session; _session = null; session?.EndTransaction(false); }
    public void Cancel() { var session = _session; _session = null; session?.EndTransaction(true); }
    public void Dispose() => Commit();
}

/// <summary>
/// Editing commands preserve native JSON, unknown extension fields and embedded images.
/// Document identity remains stable; undo replaces page/element wrappers, so clients resolve selection by ID.
/// </summary>
public sealed class HmiEditorSession : INotifyPropertyChanged
{
    private sealed record Snapshot(string Json, int PageIndex, string[] Selection);
    private sealed record Edit(string Label, Snapshot Before, Snapshot After);
    private readonly List<Edit> _undo = [], _redo = [];
    private readonly List<string> _selected = [];
    private Snapshot _transactionStart;
    private string _transactionLabel;
    private string _savedJson;
    private string _clipboard = "";
    private int _currentPageIndex;
    private int _pasteCount;
    public HmiDocument Document { get; }
    public int HistoryLimit { get; set; } = 100;
    public int CurrentPageIndex => _currentPageIndex;
    public HmiPage CurrentPage => Document.Pages.ElementAtOrDefault(_currentPageIndex);
    public IReadOnlyList<string> SelectedIds => _selected.ToArray();
    public IReadOnlyList<HmiElement> SelectedElements => CurrentPage?.AllElements.Where(element => _selected.Contains(element.Id, StringComparer.Ordinal)).ToArray() ?? [];
    public bool IsDirty => !string.Equals(_savedJson, HmiDocumentService.Serialize(Document), StringComparison.Ordinal);
    public bool CanUndo => _transactionStart == null && _undo.Count > 0;
    public bool CanRedo => _transactionStart == null && _redo.Count > 0;
    public string UndoLabel => _undo.LastOrDefault()?.Label ?? "";
    public string RedoLabel => _redo.LastOrDefault()?.Label ?? "";
    public IReadOnlyList<string> UndoHistory => _undo.AsEnumerable().Reverse().Select(edit => edit.Label).ToArray();
    public IReadOnlyList<string> RedoHistory => _redo.AsEnumerable().Reverse().Select(edit => edit.Label).ToArray();
    public bool CanPaste => _clipboard.Length > 0;
    public string ClipboardText => _clipboard;
    public bool IsInTransaction => _transactionStart != null;
    /// <summary>Only committed document changes; selecting elements never raises this event.</summary>
    public event EventHandler Changed;
    public event EventHandler SelectionChanged;
    public event PropertyChangedEventHandler PropertyChanged;

    public HmiEditorSession(HmiDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        ReindexPages();
        _savedJson = HmiDocumentService.Serialize(Document);
    }

    public void SelectPage(int index)
    {
        if (index < 0 || index >= Document.Pages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_currentPageIndex == index) return;
        _currentPageIndex = index; _selected.Clear();
        Notify(nameof(CurrentPage)); Notify(nameof(CurrentPageIndex)); NotifySelection();
    }
    public void Select(IEnumerable<string> ids, bool additive = false)
    {
        var available = CurrentPage?.AllElements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var next = (additive ? _selected : Enumerable.Empty<string>()).Concat(ids ?? []).Where(id => !string.IsNullOrEmpty(id) && available.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (_selected.SequenceEqual(next)) return;
        _selected.Clear(); _selected.AddRange(next); NotifySelection();
    }
    public void Select(string id, bool additive = false) => Select([id], additive);
    public void ClearSelection() => Select([]);
    public void SelectAll(bool includeHidden = false)
        => Select(CurrentPage?.Elements.Where(element => includeHidden || !element.Hidden).Select(element => element.Id) ?? []);

    public HmiEditTransaction BeginTransaction(string label)
    {
        if (_transactionStart != null) throw new InvalidOperationException("已有 HMI 编辑事务尚未结束。");
        _transactionStart = Capture(); _transactionLabel = string.IsNullOrWhiteSpace(label) ? "编辑" : label;
        NotifyHistory();
        return new HmiEditTransaction(this);
    }
    internal void EndTransaction(bool cancel)
    {
        if (_transactionStart == null) return;
        var before = _transactionStart; var label = _transactionLabel;
        _transactionStart = null; _transactionLabel = null;
        if (cancel) { Restore(before); NotifyDocument(); return; }
        Commit(label, before);
    }
    /// <summary>The supplied mutation has not run yet. Use this for inspector edits; exceptions roll back atomically.</summary>
    public bool Execute(string label, Action edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_transactionStart != null)
        {
            try { edit(); }
            catch { EndTransaction(true); throw; }
            return true;
        }
        var before = Capture();
        try { edit(); return Commit(label, before); }
        catch { Restore(before); NotifyDocument(); throw; }
    }
    private bool Commit(string label, Snapshot before)
    {
        PruneSelection(); ReindexPages();
        var after = Capture();
        if (before.Json == after.Json) { NotifyHistory(); NotifySelection(); return false; }
        _undo.Add(new Edit(label ?? "编辑", before, after));
        while (_undo.Count > Math.Max(1, HistoryLimit)) _undo.RemoveAt(0);
        _redo.Clear(); NotifyDocument(); return true;
    }
    public bool Undo()
    {
        if (!CanUndo) return false;
        var edit = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _redo.Add(edit);
        Restore(edit.Before); NotifyDocument(); return true;
    }
    public bool Redo()
    {
        if (!CanRedo) return false;
        var edit = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); _undo.Add(edit);
        Restore(edit.After); NotifyDocument(); return true;
    }
    public void MarkSaved() { _savedJson = HmiDocumentService.Serialize(Document); Notify(nameof(IsDirty)); }
    public void Save(string path = null)
    {
        if (IsInTransaction) throw new InvalidOperationException("请先结束当前编辑操作再保存。");
        HmiDocumentService.Save(path ?? Document.SourcePath, Document); MarkSaved();
    }
    public void Open(string path)
    {
        if (IsInTransaction) throw new InvalidOperationException("请先结束当前编辑操作再打开文件。");
        var document = HmiDocumentService.Load(path);
        ReplaceDocument(document); Document.SourcePath = document.SourcePath;
        _currentPageIndex = 0; _selected.Clear(); _undo.Clear(); _redo.Clear(); MarkSaved(); NotifyDocument();
    }

    public bool IsEditable(HmiElement element)
        => element != null && !element.Locked && Ancestors(element).All(parent => !parent.Locked);
    public bool IsEffectivelyHidden(HmiElement element)
        => element != null && (element.Hidden || Ancestors(element).Any(parent => parent.Hidden));
    public bool MoveSelection(double dx, double dy)
    {
        Finite(dx, nameof(dx)); Finite(dy, nameof(dy));
        return Execute("移动组件", () => { foreach (var element in EditableRoots()) { element.X += dx; element.Y += dy; } });
    }
    public bool ResizeElement(string id, double x, double y, double width, double height)
    {
        Finite(x, nameof(x)); Finite(y, nameof(y)); Positive(width, nameof(width)); Positive(height, nameof(height));
        return Execute("调整组件尺寸", () =>
        {
            var element = Find(id); if (!IsEditable(element)) return;
            var sx = width / Math.Max(0.000001, element.Width); var sy = height / Math.Max(0.000001, element.Height);
            foreach (var child in element.Children) Scale(child, sx, sy);
            element.X = x; element.Y = y; element.Width = width; element.Height = height;
        });
        static void Scale(HmiElement element, double sx, double sy)
        {
            element.X *= sx; element.Y *= sy; element.Width *= sx; element.Height *= sy;
            foreach (var child in element.Children) Scale(child, sx, sy);
        }
    }
    public bool SetSelectionProperty(string path, JsonNode value)
        => Execute("编辑组件属性", () => { foreach (var element in EditableRoots()) element.SetNode(path, value); });
    public bool SetPageProperty(string path, JsonNode value)
        => Execute("编辑画布属性", () => CurrentPage?.SetNode(path, value));
    public bool SetLocked(bool locked)
        => Execute(locked ? "锁定组件" : "解锁组件", () => { foreach (var element in SelectionRoots().Where(element => Ancestors(element).All(parent => !parent.Locked))) element.Locked = locked; });
    public bool SetHidden(bool hidden)
        => Execute(hidden ? "隐藏组件" : "显示组件", () => { foreach (var element in EditableRoots()) element.Hidden = hidden; });

    public HmiElement AddElement(HmiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (CurrentPage == null) throw new InvalidOperationException("没有可添加组件的页面。");
        HmiElement added = null;
        Execute("添加组件", () =>
        {
            added = CloneElements([element], true)[0];
            added.ZIndex = CurrentPage.Elements.Select(item => item.ZIndex).DefaultIfEmpty(-1).Max() + 1;
            CurrentPage.Elements.Add(added); SetSelectionSilently([added.Id]);
        });
        return added;
    }
    public string Copy()
    {
        var elements = SelectionRoots().ToArray();
        if (elements.Length == 0) return "";
        // Flatten a copied nested selection into page coordinates without changing its descendants.
        var copies = CloneElements(elements, false);
        for (var index = 0; index < copies.Count; index++)
        {
            var (x, y) = WorldPosition(elements[index]); copies[index].X = x; copies[index].Y = y;
        }
        _clipboard = new JsonObject { ["format"] = "ES-Vision-Studio.HmiClipboard.1", ["elements"] = new JsonArray(copies.Select(ElementJson).ToArray()),
            ["imageList"] = Document.Raw["imageList"]?.DeepClone(), ["sourcePath"] = Document.SourcePath }.ToJsonString();
        _pasteCount = 0; Notify(nameof(CanPaste)); Notify(nameof(ClipboardText)); return _clipboard;
    }
    public bool Cut()
    {
        var editable = EditableRoots(); if (editable.Count == 0) return false;
        var selection = _selected.ToArray();
        SetSelectionSilently(editable.Select(element => element.Id)); Copy(); SetSelectionSilently(selection);
        return DeleteSelection();
    }
    public IReadOnlyList<HmiElement> Paste(double dx = 20, double dy = 20)
    {
        if (!CanPaste) return [];
        _pasteCount++;
        return Paste(_clipboard, dx * _pasteCount, dy * _pasteCount);
    }
    public IReadOnlyList<HmiElement> Paste(string clipboardText, double dx = 20, double dy = 20)
    {
        Finite(dx, nameof(dx)); Finite(dy, nameof(dy));
        if (CurrentPage == null || string.IsNullOrWhiteSpace(clipboardText)) return [];
        var clipboard = JsonNode.Parse(clipboardText) as JsonObject;
        if (clipboard?["format"]?.GetValue<string>() != "ES-Vision-Studio.HmiClipboard.1" || clipboard["elements"] is not JsonArray elements)
            throw new FormatException("不是有效的 HMI 组件剪贴板。");
        var copies = ReadElements(elements);
        RemapIds(copies);
        Execute("粘贴组件", () =>
        {
            var z = CurrentPage.Elements.Select(element => element.ZIndex).DefaultIfEmpty(-1).Max() + 1;
            foreach (var element in copies) { element.X += dx; element.Y += dy; element.ZIndex = z++; CurrentPage.Elements.Add(element); }
            MergeImages(clipboard["imageList"] as JsonArray);
            SetSelectionSilently(copies.Select(element => element.Id));
        });
        return copies;
    }
    public IReadOnlyList<HmiElement> DuplicateSelection(double dx = 20, double dy = 20)
    {
        var old = _clipboard; var count = _pasteCount;
        try { var copied = Copy(); return string.IsNullOrEmpty(copied) ? [] : Paste(copied, dx, dy); }
        finally { _clipboard = old; _pasteCount = count; Notify(nameof(CanPaste)); Notify(nameof(ClipboardText)); }
    }
    public bool DeleteSelection()
        => Execute("删除组件", () =>
        {
            var elements = EditableRoots();
            var removed = elements.SelectMany(element => element.DescendantsAndSelf()).Select(element => element.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var element in elements) CollectionFor(element)?.Remove(element);
            foreach (var tab in Document.AllElements.Where(element => element.Type == "InputsTab"))
                if (tab.GetNode("option.dataset") is JsonArray items)
                    foreach (var item in items.OfType<JsonObject>()) if (removed.Contains(Text(item["id"]))) item["id"] = "";
            PruneSelection();
        });

    public bool BringToFront() => Reorder("置于顶层", int.MaxValue);
    public bool SendToBack() => Reorder("置于底层", int.MinValue);
    public bool MoveLayer(int step) => step == 0 ? false : Reorder(step > 0 ? "上移一层" : "下移一层", Math.Sign(step));
    private bool Reorder(string label, int direction)
        => Execute(label, () =>
        {
            var selected = EditableRoots().ToHashSet();
            foreach (var siblings in selected.Select(CollectionFor).Where(collection => collection != null).Distinct())
            {
                var sorted = siblings.Select((element, index) => (element, index)).OrderBy(item => item.element.ZIndex).ThenBy(item => item.index).Select(item => item.element).ToList();
                if (direction == int.MaxValue) sorted = sorted.Where(element => !selected.Contains(element)).Concat(sorted.Where(selected.Contains)).ToList();
                else if (direction == int.MinValue) sorted = sorted.Where(selected.Contains).Concat(sorted.Where(element => !selected.Contains(element))).ToList();
                else if (direction > 0)
                    for (var index = sorted.Count - 2; index >= 0; index--) { if (selected.Contains(sorted[index]) && !selected.Contains(sorted[index + 1])) (sorted[index], sorted[index + 1]) = (sorted[index + 1], sorted[index]); }
                else
                    for (var index = 1; index < sorted.Count; index++) { if (selected.Contains(sorted[index]) && !selected.Contains(sorted[index - 1])) (sorted[index], sorted[index - 1]) = (sorted[index - 1], sorted[index]); }
                siblings.Clear(); siblings.AddRange(sorted);
                for (var index = 0; index < siblings.Count; index++) siblings[index].ZIndex = index;
            }
        });
    public bool Align(HmiAlignment alignment, bool toPage = false)
        => Execute("对齐组件", () =>
        {
            var elements = EditableRoots(); if (elements.Count == 0 || CurrentPage == null) return;
            var positions = elements.Select(element => (element, point: WorldPosition(element))).ToArray();
            var left = toPage ? 0 : positions.Min(item => item.point.X); var top = toPage ? 0 : positions.Min(item => item.point.Y);
            var right = toPage ? CurrentPage.Width : positions.Max(item => item.point.X + item.element.Width);
            var bottom = toPage ? CurrentPage.Height : positions.Max(item => item.point.Y + item.element.Height);
            foreach (var (element, point) in positions)
                switch (alignment)
                {
                    case HmiAlignment.Left: element.X += left - point.X; break;
                    case HmiAlignment.HorizontalCenter: element.X += (left + right - element.Width) / 2 - point.X; break;
                    case HmiAlignment.Right: element.X += right - element.Width - point.X; break;
                    case HmiAlignment.Top: element.Y += top - point.Y; break;
                    case HmiAlignment.VerticalCenter: element.Y += (top + bottom - element.Height) / 2 - point.Y; break;
                    case HmiAlignment.Bottom: element.Y += bottom - element.Height - point.Y; break;
                }
        });
    public bool Distribute(bool horizontal)
        => Execute(horizontal ? "水平分布" : "垂直分布", () =>
        {
            var elements = EditableRoots().OrderBy(element => horizontal ? WorldPosition(element).X : WorldPosition(element).Y).ToArray();
            if (elements.Length < 3) return;
            var first = WorldPosition(elements[0]); var last = WorldPosition(elements[^1]);
            var start = horizontal ? first.X : first.Y;
            var end = horizontal ? last.X + elements[^1].Width : last.Y + elements[^1].Height;
            var gap = (end - start - elements.Sum(element => horizontal ? element.Width : element.Height)) / (elements.Length - 1);
            var cursor = start;
            foreach (var element in elements)
            {
                var point = WorldPosition(element);
                if (horizontal) element.X += cursor - point.X; else element.Y += cursor - point.Y;
                cursor += (horizontal ? element.Width : element.Height) + gap;
            }
        });
    public HmiElement GroupSelection(string title = "组合")
    {
        var elements = EditableRoots();
        if (elements.Count < 2) return null;
        var siblings = CollectionFor(elements[0]);
        if (siblings == null || elements.Any(element => !ReferenceEquals(CollectionFor(element), siblings)))
            throw new InvalidOperationException("只能组合处于同一层级的组件。");
        HmiElement group = null;
        Execute("组合组件", () =>
        {
            var sorted = elements.OrderBy(siblings.IndexOf).ToArray();
            var index = siblings.IndexOf(sorted[0]);
            var left = sorted.Min(element => element.X); var top = sorted.Min(element => element.Y);
            var right = sorted.Max(element => element.X + element.Width); var bottom = sorted.Max(element => element.Y + element.Height);
            group = new HmiElement(new JsonObject { ["id"] = NewId(), ["key"] = "group", ["isGroup"] = true,
                ["chartConfig"] = new JsonObject { ["key"] = "group", ["categoryName"] = "group", ["package"] = "group", ["chartFrame"] = "common", ["title"] = title, ["image"] = "", ["isRemoteComponent"] = false },
                ["option"] = new JsonObject(),
                ["attr"] = new JsonObject { ["x"] = left, ["y"] = top, ["w"] = right - left, ["h"] = bottom - top, ["minWidth"] = 0, ["minHeight"] = 0, ["zIndex"] = sorted.Max(element => element.ZIndex) },
                ["status"] = new JsonObject { ["hide"] = false, ["lock"] = false }, ["groupList"] = new JsonArray() }) { ChildCollectionKey = "groupList" };
            foreach (var element in sorted) { siblings.Remove(element); element.X -= left; element.Y -= top; group.Children.Add(element); }
            siblings.Insert(Math.Min(index, siblings.Count), group); SetSelectionSilently([group.Id]);
        });
        return group;
    }
    public IReadOnlyList<HmiElement> UngroupSelection()
    {
        var children = new List<HmiElement>();
        Execute("取消组合", () =>
        {
            foreach (var group in EditableRoots().Where(element => element.IsGroup || element.Type == "group"))
            {
                var siblings = CollectionFor(group); if (siblings == null) continue;
                var index = siblings.IndexOf(group); siblings.Remove(group);
                foreach (var child in group.Children) { child.X += group.X; child.Y += group.Y; siblings.Insert(index++, child); children.Add(child); }
                group.Children.Clear();
            }
            if (children.Count > 0) SetSelectionSilently(children.Select(element => element.Id));
        });
        return children;
    }

    public HmiPage AddPage(string title = null, bool isSubwindow = false)
    {
        HmiPage page = null;
        Execute(isSubwindow ? "新建子窗口" : "新建页面", () =>
        {
            var raw = (JsonObject)HmiDocumentService.Create(CurrentPage?.Width ?? 1920, CurrentPage?.Height ?? 1080).Pages[0].Raw.DeepClone();
            page = new HmiPage(raw, Document.Pages.Count) { IsSubwindow = isSubwindow };
            SetPageTitle(page, title ?? (isSubwindow ? "子窗口 " : "看板 ") + (Document.Pages.Count(item => item.IsSubwindow == isSubwindow) + 1));
            Document.Pages.Add(page); _currentPageIndex = Document.Pages.Count - 1; _selected.Clear();
        });
        return page;
    }
    public HmiPage DuplicatePage(int index = -1)
    {
        index = index < 0 ? CurrentPageIndex : index;
        if (index < 0 || index >= Document.Pages.Count) return null;
        HmiPage page = null;
        Execute("复制页面", () =>
        {
            var source = Document.Pages[index];
            page = new HmiPage((JsonObject)source.Raw.DeepClone(), Document.Pages.Count);
            page.Elements.AddRange(CloneElements(source.Elements, true));
            SetPageTitle(page, source.Title + " 副本");
            if (page.Raw["id"] != null) page.Raw["id"] = NewId();
            // Append rather than insert so existing native page/subwindow ordinal references keep their targets.
            Document.Pages.Add(page); _currentPageIndex = Document.Pages.Count - 1; _selected.Clear();
        });
        return page;
    }
    public bool DeletePage(int index = -1)
    {
        index = index < 0 ? CurrentPageIndex : index;
        if (index < 0 || index >= Document.Pages.Count || Document.Pages.Count <= 1) return false;
        // The runtime needs at least one normal canvas; subwindows cannot be its start page.
        if (!Document.Pages[index].IsSubwindow && Document.MainPages.Count() <= 1) return false;
        return Execute("删除页面", () =>
        {
            var page = Document.Pages[index];
            var ordinal = Document.Pages.Take(index).Count(item => item.IsSubwindow == page.IsSubwindow);
            Document.Pages.RemoveAt(index);
            RemapPageTargets(page.IsSubwindow, ordinal);
            _currentPageIndex = Math.Clamp(_currentPageIndex > index ? _currentPageIndex - 1 : _currentPageIndex, 0, Document.Pages.Count - 1);
            _selected.Clear();
        });
    }
    public bool RenamePage(string title, int index = -1)
    {
        index = index < 0 ? CurrentPageIndex : index;
        if (index < 0 || index >= Document.Pages.Count || string.IsNullOrWhiteSpace(title)) return false;
        return Execute("重命名页面", () => SetPageTitle(Document.Pages[index], title.Trim()));
    }

    public HmiElement Find(string id) => CurrentPage?.AllElements.FirstOrDefault(element => element.Id == id);
    public (double X, double Y) WorldPosition(HmiElement element)
    {
        var x = element.X; var y = element.Y;
        foreach (var parent in Ancestors(element)) { x += parent.X; y += parent.Y; }
        return (x, y);
    }
    private List<HmiElement> SelectionRoots()
    {
        var selected = SelectedElements.ToHashSet();
        return selected.Where(element => !Ancestors(element).Any(selected.Contains)).ToList();
    }
    private List<HmiElement> EditableRoots() => SelectionRoots().Where(IsEditable).ToList();
    private IEnumerable<HmiElement> Ancestors(HmiElement element)
    {
        var parent = CurrentPage?.AllElements.FirstOrDefault(item => item.Children.Contains(element));
        while (parent != null) { yield return parent; parent = CurrentPage.AllElements.FirstOrDefault(item => item.Children.Contains(parent)); }
    }
    private List<HmiElement> CollectionFor(HmiElement element)
    {
        if (CurrentPage?.Elements.Contains(element) == true) return CurrentPage.Elements;
        return CurrentPage?.AllElements.FirstOrDefault(parent => parent.Children.Contains(element))?.Children;
    }
    private Snapshot Capture() => new(HmiDocumentService.Serialize(Document), CurrentPageIndex, _selected.ToArray());
    private void Restore(Snapshot snapshot)
    {
        ReplaceDocument(HmiDocumentService.Parse(snapshot.Json));
        _currentPageIndex = Math.Clamp(snapshot.PageIndex, 0, Math.Max(0, Document.Pages.Count - 1));
        SetSelectionSilently(snapshot.Selection); PruneSelection();
    }
    private void ReplaceDocument(HmiDocument document)
    {
        Document.Raw.Clear();
        foreach (var property in document.Raw) Document.Raw[property.Key] = property.Value?.DeepClone();
        Document.Pages.Clear(); Document.Pages.AddRange(document.Pages); ReindexPages();
    }
    private void ReindexPages() { for (var index = 0; index < Document.Pages.Count; index++) Document.Pages[index].Index = index; }
    private void PruneSelection()
    {
        var ids = CurrentPage?.AllElements.Select(element => element.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        _selected.RemoveAll(id => !ids.Contains(id));
    }
    private void SetSelectionSilently(IEnumerable<string> ids) { _selected.Clear(); _selected.AddRange(ids.Distinct(StringComparer.Ordinal)); }
    private void NotifySelection() { Notify(nameof(SelectedIds)); Notify(nameof(SelectedElements)); SelectionChanged?.Invoke(this, EventArgs.Empty); }
    private void NotifyHistory() { Notify(nameof(CanUndo)); Notify(nameof(CanRedo)); Notify(nameof(UndoLabel)); Notify(nameof(RedoLabel)); Notify(nameof(UndoHistory)); Notify(nameof(RedoHistory)); Notify(nameof(IsInTransaction)); Notify(nameof(IsDirty)); }
    private void NotifyDocument()
    {
        NotifyHistory(); Notify(nameof(Document)); Notify(nameof(CurrentPage)); Notify(nameof(CurrentPageIndex));
        Changed?.Invoke(this, EventArgs.Empty); NotifySelection();
    }
    private void Notify([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static void Finite(double value, string name) { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name); }
    private static void Positive(double value, string name) { if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name); }
    private string NewId()
    {
        string id;
        do id = Guid.NewGuid().ToString("N"); while (Document.AllElements.Any(element => element.Id == id));
        return id;
    }
    private List<HmiElement> CloneElements(IEnumerable<HmiElement> elements, bool newIds)
    {
        var clones = ReadElements(new JsonArray(elements.Select(ElementJson).ToArray()));
        if (newIds) RemapIds(clones);
        return clones;
    }
    private static JsonNode ElementJson(HmiElement element)
    {
        var raw = (JsonObject)element.Raw.DeepClone();
        if (element.ChildCollectionKey != null || element.Children.Count > 0)
            raw[element.ChildCollectionKey ?? "groupList"] = new JsonArray(element.Children.Select(ElementJson).ToArray());
        return raw;
    }
    private static List<HmiElement> ReadElements(JsonArray elements)
    {
        var result = new List<HmiElement>();
        foreach (var item in elements)
        {
            if (item is not JsonObject raw) throw new FormatException("HMI 组件必须是对象。");
            var element = new HmiElement((JsonObject)raw.DeepClone());
            foreach (var key in new[] { "groupList", "componentList", "children" })
                if (element.Raw[key] is JsonArray children) { element.ChildCollectionKey = key; element.Children.AddRange(ReadElements(children)); break; }
            result.Add(element);
        }
        return result;
    }
    private void RemapIds(IEnumerable<HmiElement> elements)
    {
        var all = elements.SelectMany(element => element.DescendantsAndSelf()).ToArray();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var assigned = new Dictionary<HmiElement, string>();
        foreach (var element in all)
        {
            var id = NewId(); assigned[element] = id;
            if (!string.IsNullOrEmpty(element.Id)) ids.TryAdd(element.Id, id);
        }
        foreach (var element in all)
        {
            element.Id = assigned[element];
            if (element.Type == "InputsTab" && element.GetNode("option.dataset") is JsonArray tabs)
                foreach (var tab in tabs.OfType<JsonObject>()) if (ids.TryGetValue(Text(tab["id"]), out var target)) tab["id"] = target;
            RemapReferences(element.Raw);
        }
        void RemapReferences(JsonNode node)
        {
            if (node is JsonObject map)
                foreach (var property in map.ToArray())
                {
                    if (property.Key is "targetId" or "componentId" or "parentId" or "target" && ids.TryGetValue(Text(property.Value), out var reference)) map[property.Key] = reference;
                    else if (property.Value is JsonObject or JsonArray) RemapReferences(property.Value);
                }
            else if (node is JsonArray array) foreach (var child in array) if (child != null) RemapReferences(child);
        }
    }
    private void MergeImages(JsonArray source)
    {
        if (source == null || source.Count == 0) return;
        if (Document.Raw["imageList"] is not JsonArray images) Document.Raw["imageList"] = images = new JsonArray();
        foreach (var image in source) if (!images.Any(existing => JsonNode.DeepEquals(existing, image))) images.Add(image?.DeepClone());
    }
    private void RemapPageTargets(bool subwindow, int removedOrdinal)
    {
        foreach (var element in Document.AllElements)
        {
            if (subwindow && element.Type == "SubwindowButton") Adjust(element.Option, "bindSubwindowsIndex");
            if (element.GetNode("option.iconGroup") is not JsonArray icons) continue;
            foreach (var icon in icons.OfType<JsonObject>())
                if (icon["events"] is JsonArray events)
                    foreach (var action in events.OfType<JsonObject>())
                    {
                        var name = Text(action["event"]);
                        if (subwindow ? name is "openSubwindow" or "subwindow" : name == "bulletinBoard") Adjust(action, "target");
                    }
        }
        void Adjust(JsonObject obj, string key)
        {
            if (!int.TryParse(Text(obj[key]), out var index) || index < removedOrdinal) return;
            var value = index == removedOrdinal ? -1 : index - 1;
            obj[key] = obj[key] is JsonValue scalar && scalar.TryGetValue<string>(out _) ? JsonValue.Create(value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : JsonValue.Create(value);
        }
    }
    private static void SetPageTitle(HmiPage page, string title)
    {
        if (page.Raw.ContainsKey("name")) page.Raw["name"] = title;
        page.Title = title;
    }
    private static string Text(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";
}
