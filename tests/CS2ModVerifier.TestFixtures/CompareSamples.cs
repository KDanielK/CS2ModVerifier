// Source for the DLL-vs-source comparison tests. Each construct here compiles to metadata that the
// comparer must map back to this source. Nothing in this file is ever called.
#pragma warning disable CS0067 // event never used
#pragma warning disable CS1998 // async method without await

namespace CS2ModVerifier.TestFixtures.Compare
{
    public class Overloads
    {
        private static readonly int[] Table = [1, 2, 3];

        public void Refs(ref int a, out int b, in int c) => b = a + c + Table[0];

        public int Sum(params int[] values) => values.Length;

        public int? Maybe(int? value, string? text) => value;

        public (int, string) Pair((int A, string B) pair) => pair;

        public int Count(System.Collections.Generic.List<int> list, int[,] grid) => list.Count + grid.Length;

        public T Echo<T>(T value) => value;

        public long OnlyOverload(int value) => value;
    }

    public class Box<T>
    {
        public T Value(T item) => item;

        public class Inner
        {
            public T Get(T item) => item;
        }
    }

    public class Members : System.IDisposable
    {
        private readonly string[] items = new string[4];

        public int Plain { get; set; }

        public string Init { get; init; } = string.Empty;

        public int Computed => Plain * 2;

        public string this[int index]
        {
            get => items[index];
            set => items[index] = value;
        }

        public event System.EventHandler? Changed;

        public event System.EventHandler Custom
        {
            add { }
            remove { }
        }

        void System.IDisposable.Dispose()
        {
        }

        ~Members()
        {
        }
    }

    public readonly struct Money(decimal amount)
    {
        public decimal Amount { get; } = amount;

        public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);

        public static Money operator -(Money a) => new(-a.Amount);

        public static bool operator ==(Money a, Money b) => a.Amount == b.Amount;

        public static bool operator !=(Money a, Money b) => !(a == b);

        public static implicit operator decimal(Money m) => m.Amount;

        public override bool Equals(object? obj) => obj is Money other && other.Amount == Amount;

        public override int GetHashCode() => Amount.GetHashCode();
    }

    public record Person(string Name, int Age)
    {
        public string Describe() => $"{Name} ({Age})";
    }

    public record struct Point(int X, int Y);

    public struct Plain
    {
        public int Twice(int value) => value * 2;
    }

    public interface IShape
    {
        double Area();

        string Name { get; }
    }

    public enum Color
    {
        Red,
        Green,
    }

    public delegate int Transform(int value);

    public class Generated
    {
        public System.Func<int, int> Capture(int offset) => x => x + offset;

        public int WithLocal(int value)
        {
            return Twice(value);

            static int Twice(int v) => v * 2;
        }

        public async System.Threading.Tasks.Task<int> LaterAsync() => 1;

        public System.Collections.Generic.IEnumerable<int> Numbers()
        {
            yield return 1;
        }
    }

    public class Greeter(string name)
    {
        public string Greet() => "Hello " + name;
    }

    public static class Extensions
    {
        public static int Doubled(this int value) => value * 2;
    }

    public class NullableStruct
    {
        public int Use(Plain? value) => value.HasValue ? 1 : 0;
    }
}
