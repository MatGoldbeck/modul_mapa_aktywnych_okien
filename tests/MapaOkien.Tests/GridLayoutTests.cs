using MapaOkien.Backend.Services;
using Xunit;

namespace MapaOkien.Tests;

public class GridSizeTests
{
    [Theory]
    [InlineData(1366, 768, 5)]
    [InlineData(1920, 1080, 5)]
    [InlineData(1920, 1200, 6)] // wysokość ponad próg 1080
    [InlineData(2560, 1080, 6)] // szerokość ponad próg 1920
    [InlineData(2560, 1440, 6)]
    [InlineData(3440, 1440, 8)]
    [InlineData(3840, 2160, 8)]
    [InlineData(5120, 2880, 8)] // powyżej tabeli – największa siatka
    public void Size_follows_resolution_table(int w, int h, int expected) =>
        Assert.Equal(expected, GridSize.For(w, h));

    [Fact]
    public void Overflow_message_only_when_nodes_exceed_cells()
    {
        Assert.Null(GridSize.OverflowMessage(25, 25));
        Assert.Equal("Liczba nodów (27) przekracza liczbę pól siatki (25). Nie wyświetlono 2 nodów.",
            GridSize.OverflowMessage(27, 25));
    }
}

public class GridLayoutTests
{
    [Fact]
    public void New_nodes_fill_their_edge_in_order()
    {
        var g = new GridLayout(5);
        Assert.Equal(new Cell(0, 0), g.Add("tab1", Edge.Top));
        Assert.Equal(new Cell(0, 1), g.Add("tab2", Edge.Top));
        Assert.Equal(new Cell(4, 0), g.Add("explorer", Edge.Bottom));
        Assert.Equal(new Cell(1, 0), g.Add("word", Edge.Left)); // (0,0) zajęte przez kartę
        Assert.Equal(new Cell(0, 4), g.Add("app", Edge.Right));
    }

    [Fact]
    public void Full_edge_goes_to_nearest_free_cell()
    {
        var g = new GridLayout(3);
        for (int i = 0; i < 3; i++) g.Add($"t{i}", Edge.Top);
        // górny wiersz pełny → najbliżej góry jest wiersz 1
        Assert.Equal(new Cell(1, 0), g.Add("t3", Edge.Top));
    }

    [Fact]
    public void Remembered_cell_is_restored_when_free_and_in_range()
    {
        var saved = new Dictionary<string, SavedCell>
        {
            ["word"] = new(2, 3, DateTime.UtcNow),
            ["huge"] = new(7, 7, DateTime.UtcNow), // poza siatką 5×5
        };
        var g = new GridLayout(5, saved);
        Assert.Equal(new Cell(2, 3), g.Add("word", Edge.Left));
        Assert.Equal(new Cell(0, 4), g.Add("huge", Edge.Right)); // ignoruje (7,7)
    }

    [Fact]
    public void Remembered_cell_taken_by_other_node_falls_back_to_edge()
    {
        var g = new GridLayout(5, new Dictionary<string, SavedCell> { ["a"] = new(0, 0, DateTime.UtcNow) });
        g.Add("b", Edge.Top); // zajmuje (0,0)
        Assert.Equal(new Cell(1, 0), g.Add("a", Edge.Left));
    }

    [Fact]
    public void Closed_node_keeps_remembered_cell_for_next_time()
    {
        var g = new GridLayout(5);
        g.Add("word", Edge.Left);
        g.Move("word", new Cell(2, 2));
        g.Remove("word");
        Assert.Null(g.CellOf("word"));
        Assert.Equal(new Cell(2, 2), g.Add("word", Edge.Left));
    }

    [Fact]
    public void Move_to_occupied_cell_swaps_nodes()
    {
        var g = new GridLayout(5);
        g.Add("a", Edge.Top); // (0,0)
        g.Add("b", Edge.Top); // (0,1)
        Assert.True(g.Move("a", new Cell(0, 1)));
        Assert.Equal(new Cell(0, 1), g.CellOf("a"));
        Assert.Equal(new Cell(0, 0), g.CellOf("b"));
        Assert.Equal(0, g.Remembered["b"].Col);
    }

    [Fact]
    public void Move_outside_grid_is_rejected()
    {
        var g = new GridLayout(5);
        g.Add("a", Edge.Top);
        Assert.False(g.Move("a", new Cell(5, 0)));
        Assert.Equal(new Cell(0, 0), g.CellOf("a"));
    }

    [Fact]
    public void Overflow_queues_nodes_and_fills_freed_cells()
    {
        var g = new GridLayout(1);
        Assert.NotNull(g.Add("a", Edge.Top));
        Assert.Null(g.Add("b", Edge.Top));
        Assert.Null(g.Add("c", Edge.Top));
        Assert.Equal(new[] { "b", "c" }, g.Overflow);

        g.Remove("a");
        Assert.Equal(new Cell(0, 0), g.CellOf("b"));
        Assert.Equal(new[] { "c" }, g.Overflow);
    }

    [Fact]
    public void Rename_keeps_cell_and_remembers_new_key()
    {
        var g = new GridLayout(5);
        g.Add("code|a.md", Edge.Right);
        g.Move("code|a.md", new Cell(3, 3));
        Assert.True(g.Rename("code|a.md", "code|b.md"));
        Assert.Equal(new Cell(3, 3), g.CellOf("code|b.md"));
        Assert.Null(g.CellOf("code|a.md"));
        Assert.Equal(3, g.Remembered["code|b.md"].Row);
    }

    [Fact]
    public void Rename_to_existing_key_is_refused()
    {
        var g = new GridLayout(5);
        g.Add("a", Edge.Top);
        g.Add("b", Edge.Top);
        Assert.False(g.Rename("a", "b"));
    }

    [Fact]
    public void Remembered_entries_are_capped_dropping_oldest_inactive()
    {
        var t = new DateTime(2026, 1, 1);
        var g = new GridLayout(2, now: () => t = t.AddSeconds(1));
        for (int i = 0; i < GridLayout.MaxRemembered + 10; i++)
        {
            g.Add($"k{i}", Edge.Top);
            g.Remove($"k{i}");
        }
        Assert.Equal(GridLayout.MaxRemembered, g.Remembered.Count);
        Assert.False(g.Remembered.ContainsKey("k0"));
        Assert.True(g.Remembered.ContainsKey($"k{GridLayout.MaxRemembered + 9}"));
    }
}
