using System;

namespace SqlServerSimulator;

internal class LazyRegeneratingWeakReference<T> : Lazy<WeakReference<T>>
    where T : class
{
    private readonly Func<T> regenerator;

    public LazyRegeneratingWeakReference(Func<T> regenerator)
        : base(() => new WeakReference<T>(regenerator(), false), true)
    {
        this.regenerator = regenerator;
    }

    public T Reference
    {
        get
        {
            var reference = this.Value;

            if (!reference.TryGetTarget(out var value))
                reference.SetTarget(value = regenerator());

            return value;
        }
    }

    public static implicit operator T(LazyRegeneratingWeakReference<T> reference) => reference.Reference;
}
