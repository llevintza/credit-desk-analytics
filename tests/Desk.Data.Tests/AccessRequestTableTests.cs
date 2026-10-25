using Desk.Data.App;
using Desk.Data.App.Migrations;
using Desk.Data.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Desk.Data.Tests;

/// <summary>
/// ADR-0029, #538: <c>app.access_request</c>. The model, the expand-only migration and, on a migrated Postgres 17,
/// the partial unique index that allows one pending request per address.
/// </summary>
public sealed class AccessRequestTableTests(MigratedDatabase db) : IClassFixture<MigratedDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_model_maps_the_columns_of_the_issue()
    {
        using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost").Options);
        var request = ctx.Model.FindEntityType(typeof(AccessRequest))!;
        Assert.Equal(("app", "access_request"), (request.GetSchema(), request.GetTableName()));
        Assert.Equal(
            ["account_id", "created_at", "decided_at", "decided_by", "display_name", "email", "id", "purpose", "status"],
            request.GetProperties().Select(p => p.GetColumnName()).Order());
        Assert.Equal("citext", request.FindProperty(nameof(AccessRequest.Email))!.GetColumnType());
        Assert.Equal(500, request.FindProperty(nameof(AccessRequest.DisplayName))!.GetMaxLength());
        Assert.Equal(500, request.FindProperty(nameof(AccessRequest.Purpose))!.GetMaxLength());
        var fk = Assert.Single(request.GetForeignKeys());
        Assert.Equal(typeof(DeskUser), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.SetNull, fk.DeleteBehavior);
    }

    [Fact]
    public void The_migration_only_adds_and_creates_the_index_rerun_safely()
    {
        // README §14.4 expand only: nothing dropped, renamed or altered in Up, and no rows inserted.
        var migration = new AccessRequests();
        Assert.All(migration.UpOperations, o => Assert.IsNotType<DropTableOperation>(o));
        Assert.DoesNotContain(migration.UpOperations, o => o is DropColumnOperation or RenameColumnOperation or RenameTableOperation
            or AlterColumnOperation or InsertDataOperation or UpdateDataOperation or DeleteDataOperation);
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal(("app", "access_request"), (table.Schema, table.Name));
        var sql = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Equal(AccessRequests.PendingEmailIndexSql, sql);
        Assert.StartsWith("CREATE UNIQUE INDEX IF NOT EXISTS ux_access_request_pending_email ON app.access_request (lower(email)) WHERE status = 'pending'", sql);
        Assert.Contains(migration.DownOperations.OfType<SqlOperation>(), o => o.Sql == "DROP INDEX IF EXISTS app.ux_access_request_pending_email");
    }

    [Fact]
    public async Task A_second_pending_request_for_the_same_address_in_any_case_is_rejected()
    {
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await using var conn = await db.OpenAsync();
        await InsertAsync(conn, email, AccessRequestStatus.Pending);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, email.ToUpperInvariant(), AccessRequestStatus.Pending));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal(AppDbContext.PendingEmailIndex, ex.ConstraintName);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task A_decided_request_does_not_block_a_new_pending_one(string decided)
    {
        var email = $"again-{Guid.NewGuid():N}@example.com";
        await using var conn = await db.OpenAsync();
        await InsertAsync(conn, email, decided);
        await InsertAsync(conn, email, AccessRequestStatus.Pending);
        await InsertAsync(conn, email, decided);

        await using var count = new NpgsqlCommand("SELECT count(*) FROM app.access_request WHERE email = @e::citext", conn);
        // citext compares case-insensitively only against citext: a text parameter would pick text = text.
        count.Parameters.AddWithValue("e", email.ToUpperInvariant());
        Assert.Equal(3L, await count.ExecuteScalarAsync(Ct));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("PENDING")]
    public async Task An_unknown_status_is_rejected(string status)
    {
        await using var conn = await db.OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, $"s-{Guid.NewGuid():N}@example.com", status));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_access_request_status", ex.ConstraintName);
    }

    [Fact]
    public async Task An_email_longer_than_254_characters_is_rejected()
    {
        await using var conn = await db.OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, new string('a', 250) + "@x.io", AccessRequestStatus.Pending));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_access_request_email_length", ex.ConstraintName);
    }

    [Fact]
    public async Task Deleting_the_account_unlinks_its_request()
    {
        var id = Guid.NewGuid();
        var email = $"linked-{id:N}@example.com";
        await using var conn = await db.OpenAsync();
        await Exec(conn, "INSERT INTO auth.users (id, user_name, email, email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count, is_disabled, expires_at) "
            + "VALUES (@id, @e, @e, true, false, false, true, 0, false, now())", ("id", id), ("e", email));
        await Exec(conn, "INSERT INTO app.access_request (email, status, created_at, account_id) VALUES (@e, 'approved', now(), @id)", ("e", email), ("id", id));

        await Exec(conn, "DELETE FROM auth.users WHERE id = @id", ("id", id));

        await using var read = new NpgsqlCommand("SELECT account_id IS NULL FROM app.access_request WHERE email = @e", conn);
        read.Parameters.AddWithValue("e", email);
        Assert.Equal(true, await read.ExecuteScalarAsync(Ct));
    }

    private static Task InsertAsync(NpgsqlConnection conn, string email, string status) =>
        Exec(conn, "INSERT INTO app.access_request (email, status, created_at) VALUES (@e, @s, now())", ("e", email), ("s", status));

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}
