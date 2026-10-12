namespace MapaOkien.Backend.Services;

/// <summary>Krawędź siatki, przy której pojawiają się nowe nody danego typu (specyfikacja pkt 5).</summary>
public enum Edge { Top, Left, Bottom, Right }

public readonly record struct Cell(int Row, int Col);

/// <summary>Zapamiętane pole noda (także nodów, których okna są obecnie zamknięte).</summary>
public sealed record SavedCell(int Row, int Col, DateTime Updated);

/// <summary>
/// Rozmieszczenie nodów na siatce N×N. Czysta logika bez Win32 – testowana w tests/.
///
/// Zasady (docs/specyfikacja.md, pkt 4–5):
///  - nod ze znanym kluczem wraca do zapamiętanego pola, jeśli jest wolne i mieści się w siatce,
///  - w przeciwnym razie trafia do pierwszego wolnego pola przy krawędzi swojego typu,
///  - gdy krawędź jest pełna – do wolnego pola najbliższego tej krawędzi,
///  - gdy siatka jest pełna – do kolejki przepełnienia; wchodzi, gdy zwolni się pole,
///  - przeniesienie na zajęte pole zamienia nody miejscami.
/// </summary>
public sealed class GridLayout
{
    public const int MaxRemembered = 1000;

    private readonly Dictionary<string, Cell> _placed = new();
    private readonly Dictionary<Cell, string> _byCell = new();
    private readonly Dictionary<string, Edge> _edges = new();
    private readonly List<string> _overflow = new();
    private readonly Dictionary<string, SavedCell> _remembered;
    private readonly Func<DateTime> _now;

    public GridLayout(int size, IDictionary<string, SavedCell>? remembered = null, Func<DateTime>? now = null)
    {
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        _remembered = remembered != null ? new(remembered) : new();
        _now = now ?? (() => DateTime.UtcNow);
    }

    public int Size { get; }
    public int Cells => Size * Size;

    /// <summary>Nody, które się nie zmieściły, w kolejności pojawienia się.</summary>
    public IReadOnlyList<string> Overflow => _overflow;

    public IReadOnlyDictionary<string, SavedCell> Remembered => _remembered;

    public bool Contains(string key) => _placed.ContainsKey(key) || _overflow.Contains(key);

    public Cell? CellOf(string key) => _placed.TryGetValue(key, out var c) ? c : null;

    /// <summary>Dodaje nod. Zwraca pole albo null, gdy nod trafił do przepełnienia.</summary>
    public Cell? Add(string key, Edge edge)
    {
        if (_placed.TryGetValue(key, out var existing)) return existing;
        if (_overflow.Contains(key)) return null;
        _edges[key] = edge;

        var cell = FindCell(key, edge);
        if (cell == null)
        {
            _overflow.Add(key);
            return null;
        }
        Place(key, cell.Value);
        return cell;
    }

    /// <summary>Usuwa nod (okno zamknięte). Zapamiętane pole zostaje. Zwolnione pole dostaje kolejka.</summary>
    public void Remove(string key)
    {
        if (_overflow.Remove(key))
        {
            _edges.Remove(key);
            return;
        }
        if (!_placed.Remove(key, out var cell)) return;
        _byCell.Remove(cell);
        _edges.Remove(key);
        FillFromOverflow();
    }

    /// <summary>
    /// Zmiana klucza tego samego noda (np. zmiana tytułu okna albo adresu karty) – nod zostaje
    /// w swoim polu. Zwraca false, gdy nowy klucz już istnieje (wtedy wołający usuwa stary nod).
    /// </summary>
    public bool Rename(string oldKey, string newKey)
    {
        if (oldKey == newKey) return true;
        if (Contains(newKey) || !Contains(oldKey)) return false;

        if (_edges.Remove(oldKey, out var edge)) _edges[newKey] = edge;

        var i = _overflow.IndexOf(oldKey);
        if (i >= 0)
        {
            _overflow[i] = newKey;
            return true;
        }
        var cell = _placed[oldKey];
        _placed.Remove(oldKey);
        _placed[newKey] = cell;
        _byCell[cell] = newKey;
        Remember(newKey, cell);
        return true;
    }

    /// <summary>Przeniesienie noda przez użytkownika. Zajęte pole → zamiana miejscami.</summary>
    public bool Move(string key, Cell target)
    {
        if (!InRange(target) || !_placed.TryGetValue(key, out var from)) return false;
        if (from == target) return true;

        if (_byCell.TryGetValue(target, out var other))
        {
            _placed[other] = from;
            _byCell[from] = other;
            Remember(other, from);
        }
        else
        {
            _byCell.Remove(from);
        }
        _placed[key] = target;
        _byCell[target] = key;
        Remember(key, target);
        return true;
    }

    public IEnumerable<(string Key, Cell Cell)> Placed => _placed.Select(p => (p.Key, p.Value));

    // --- wewnętrzne -------------------------------------------------------

    private void Place(string key, Cell cell)
    {
        _placed[key] = cell;
        _byCell[cell] = key;
        Remember(key, cell);
    }

    private void FillFromOverflow()
    {
        while (_overflow.Count > 0 && _placed.Count < Cells)
        {
            var key = _overflow[0];
            var cell = FindCell(key, _edges.GetValueOrDefault(key, Edge.Right));
            if (cell == null) return;
            _overflow.RemoveAt(0);
            Place(key, cell.Value);
        }
    }

    private Cell? FindCell(string key, Edge edge)
    {
        if (_remembered.TryGetValue(key, out var saved))
        {
            var c = new Cell(saved.Row, saved.Col);
            if (InRange(c) && !_byCell.ContainsKey(c)) return c;
        }

        foreach (var c in EdgeCells(edge))
            if (!_byCell.ContainsKey(c)) return c;

        Cell? best = null;
        int bestDistance = int.MaxValue;
        for (int r = 0; r < Size; r++)
        for (int col = 0; col < Size; col++)
        {
            var c = new Cell(r, col);
            if (_byCell.ContainsKey(c)) continue;
            int d = DistanceToEdge(c, edge);
            if (d < bestDistance)
            {
                best = c;
                bestDistance = d;
            }
        }
        return best;
    }

    public IEnumerable<Cell> EdgeCells(Edge edge)
    {
        for (int i = 0; i < Size; i++)
        {
            yield return edge switch
            {
                Edge.Top => new Cell(0, i),
                Edge.Bottom => new Cell(Size - 1, i),
                Edge.Left => new Cell(i, 0),
                _ => new Cell(i, Size - 1),
            };
        }
    }

    private int DistanceToEdge(Cell c, Edge edge) => edge switch
    {
        Edge.Top => c.Row,
        Edge.Bottom => Size - 1 - c.Row,
        Edge.Left => c.Col,
        _ => Size - 1 - c.Col,
    };

    private bool InRange(Cell c) => c.Row >= 0 && c.Col >= 0 && c.Row < Size && c.Col < Size;

    private void Remember(string key, Cell cell)
    {
        _remembered[key] = new SavedCell(cell.Row, cell.Col, _now());
        if (_remembered.Count <= MaxRemembered) return;

        // Najdawniej używane wpisy (zwykle okna dawno zamknięte) wypadają jako pierwsze.
        var active = new HashSet<string>(_placed.Keys);
        foreach (var old in _remembered.Where(r => !active.Contains(r.Key))
                     .OrderBy(r => r.Value.Updated).Take(_remembered.Count - MaxRemembered).ToList())
            _remembered.Remove(old.Key);
    }
}
