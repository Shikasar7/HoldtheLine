using HoldTheLine.Server.Data;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B0: the persistent-identity store — idempotent schema, register→restore round-trip,
/// and secret verification.</summary>
public class AccountStoreTests
{
    [Fact]
    public void Schema_creation_is_idempotent()
    {
        using var db = new Db(null);
        _ = new AccountStore(db);
        _ = new AccountStore(db); // second CREATE TABLE IF NOT EXISTS must not throw
        var store = new AccountStore(db);
        Assert.Null(store.Find("nobody"));
    }

    [Fact]
    public void Register_then_restore_round_trips()
    {
        using var db = new Db(null);
        var store = new AccountStore(db);

        var (first, acct) = store.RegisterOrRestore("g1", "secret-1", "Alice");
        Assert.Equal(AccountStore.Outcome.Registered, first);
        Assert.Equal("Alice", acct.Name);

        var (second, acct2) = store.RegisterOrRestore("g1", "secret-1", "Alice Renamed");
        Assert.Equal(AccountStore.Outcome.Restored, second);
        Assert.Equal("Alice Renamed", acct2.Name); // name refreshes on restore

        Assert.Equal("Alice Renamed", store.Find("g1")!.Name);
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        using var db = new Db(null);
        var store = new AccountStore(db);
        store.RegisterOrRestore("g1", "correct", "Alice");

        var (outcome, _) = store.RegisterOrRestore("g1", "WRONG", "Mallory");
        Assert.Equal(AccountStore.Outcome.BadSecret, outcome);
        Assert.Equal("Alice", store.Find("g1")!.Name); // name unchanged by the failed attempt
    }

    [Fact]
    public void Distinct_guests_are_independent()
    {
        using var db = new Db(null);
        var store = new AccountStore(db);
        Assert.Equal(AccountStore.Outcome.Registered, store.RegisterOrRestore("g1", "s1", "A").Outcome);
        Assert.Equal(AccountStore.Outcome.Registered, store.RegisterOrRestore("g2", "s2", "B").Outcome);
        Assert.Equal(AccountStore.Outcome.Restored, store.RegisterOrRestore("g1", "s1", "A").Outcome);
    }
}
