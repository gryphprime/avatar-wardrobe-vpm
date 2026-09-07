using System;
using OutfitToggleGenerator;
class Item { public int id; public string guid; public string path; }
class Program
{
    static int count;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); count++; }
    static void Main()
    {
        var a = new Item { id = 1, guid = "same-guid", path = "Same name" };
        var b = new Item { id = 2, guid = "same-guid", path = "Same name" };
        Check(WardrobeEditPolicy.ExactInstance(new[] {a,b}, "2", x => x.id) == b, "Second duplicate must be selected by identity");
        Check(WardrobeEditPolicy.ExactInstance(new[] {a,b}, "", x => x.id) == null, "Missing identity must fail closed");
        Check(WardrobeEditPolicy.ExactInstance(new[] {a}, "2", x => x.id) == null, "Stale/foreign instance must be rejected");
        Check(WardrobeEditPolicy.ContextMatches("session", "session", 1, 1), "Matching context accepted");
        Check(!WardrobeEditPolicy.ContextMatches("session", "", 1, 1), "No session rejected");
        Check(!WardrobeEditPolicy.ContextMatches("new-session", "old-session", 1, 1), "Restart invalidates context");
        Check(!WardrobeEditPolicy.ContextMatches("session", "session", 2, 1), "Wrong avatar rejected");
        Check(!WardrobeEditPolicy.ContextMatches("session", "session", 0, 0), "Missing avatar rejected");
        Check(WardrobeEditPolicy.FitMatches("v1","v1","base1","base1","shape1","shape1"), "Scoped fit accepted");
        Check(!WardrobeEditPolicy.FitMatches("v1","v2","base1","base1","shape1","shape1"), "Asset update invalidates trust");
        Check(!WardrobeEditPolicy.FitMatches("v1","v1","base1","base2","shape1","shape1"), "Base update invalidates trust");
        Check(!WardrobeEditPolicy.FitMatches("v1","v1","base1","base1","shape1","shape2"), "Shape change invalidates trust");
        Check(!WardrobeEditPolicy.FitMatches("v1","v1","","","shape1","shape1"), "Unknown base version rejected");
        Console.WriteLine($"PASS: {count} wardrobe identity, context, and fit assertions.");
    }
}
