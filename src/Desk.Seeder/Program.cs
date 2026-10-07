using Desk.Seeder;

// Exit codes: 0 ok (seeded or skipped), 1 bad arguments / not migrated, 2 over the size budget.
return await SeedApp.RunAsync(args, CancellationToken.None);
