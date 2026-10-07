using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Data.App.Migrations
{
    /// <inheritdoc />
    public partial class DataSchemas : Migration
    {
        // Data schemas (README §5.1). Owned by migrations; populated only by Desk.Seeder.
        // Every source schema can later move to its own database (README §2).
        internal const string SchemasSql = """
            CREATE SCHEMA IF NOT EXISTS reference;
            CREATE SCHEMA IF NOT EXISTS core;
            CREATE SCHEMA IF NOT EXISTS market;
            CREATE SCHEMA IF NOT EXISTS surveillance;
            CREATE SCHEMA IF NOT EXISTS pricing;

            CREATE TABLE reference.issuer (issuer_id integer PRIMARY KEY, name text NOT NULL, country text NOT NULL);
            CREATE TABLE reference.servicer (servicer_id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE reference.trustee (trustee_id integer PRIMARY KEY, name text NOT NULL);
            CREATE TABLE reference.rating_scale (agency text NOT NULL, rating text NOT NULL, rank integer NOT NULL, PRIMARY KEY (agency, rating));
            CREATE TABLE reference.sector (sector text NOT NULL, sub_sector text NOT NULL, PRIMARY KEY (sector, sub_sector));

            CREATE TABLE core.fund (
                fund_id integer PRIMARY KEY, name text NOT NULL, inception_date date NOT NULL, strategy text NOT NULL);
            CREATE TABLE core.portfolio (
                portfolio_id integer PRIMARY KEY, fund_id integer NOT NULL REFERENCES core.fund (fund_id),
                name text NOT NULL, manager text NOT NULL, benchmark text NOT NULL);
            CREATE TABLE core.deal (
                deal_id integer PRIMARY KEY, name text NOT NULL, sector text NOT NULL, sub_sector text NOT NULL,
                issuer_id integer NOT NULL, servicer_id integer NOT NULL, trustee_id integer NOT NULL,
                vintage integer NOT NULL, closing_date date NOT NULL, collateral_type text NOT NULL,
                original_balance numeric(18,2) NOT NULL, currency text NOT NULL, status text NOT NULL);
            CREATE TABLE core.bond (
                bond_id integer PRIMARY KEY, deal_id integer NOT NULL REFERENCES core.deal (deal_id),
                cusip char(9) NOT NULL UNIQUE, class text NOT NULL, seniority_rank integer NOT NULL,
                original_balance numeric(18,2) NOT NULL, current_balance numeric(18,2) NOT NULL, factor double precision NOT NULL,
                coupon_type text NOT NULL, coupon_or_margin double precision NULL, coupon_index text NULL,
                rating_sp text NULL, rating_moodys text NULL, rating_fitch text NULL,
                legal_final date NOT NULL, expected_maturity date NOT NULL);
            -- "Any one bond per deal" (P4) = LATERAL ... ORDER BY seniority_rank, bond_id LIMIT 1: one index seek per deal.
            CREATE INDEX ix_bond_deal_seniority ON core.bond (deal_id, seniority_rank, bond_id);

                    CREATE TABLE core.position_snapshot (
                as_of_date date NOT NULL,
                position_id bigint NOT NULL,
                portfolio_id integer NOT NULL,
                fund_id integer NOT NULL,
                bond_id integer NOT NULL,
                deal_id integer NOT NULL,
                cusip text NOT NULL,
                deal_name text NOT NULL,
                class text,
                sector text NOT NULL,
                sub_sector text,
                vintage integer,
                rating_composite text,
                currency text,
                face numeric(18,2),
                current_face numeric(18,2),
                factor double precision,
                book_price double precision,
                book_value numeric(18,2),
                market_value numeric(18,2),
                accrued numeric(18,2),
                unrealized_pnl numeric(18,2),
                pct_of_portfolio_mv double precision,
                price double precision,
                price_chg_1d double precision,
                price_chg_1w double precision,
                price_chg_1m double precision,
                yield double precision,
                spread_bp double precision,
                oas_bp double precision,
                dm_bp double precision,
                spread_chg_1d_bp double precision,
                spread_chg_1w_bp double precision,
                spread_chg_1m_bp double precision,
                z_spread_bp double precision,
                price_source text,
                vendor_dispersion_bp double precision,
                mod_duration double precision,
                eff_duration double precision,
                convexity double precision,
                dv01 numeric(18,2),
                krd_2y numeric(18,2),
                krd_5y numeric(18,2),
                krd_10y numeric(18,2),
                krd_20y numeric(18,2),
                krd_30y numeric(18,2),
                rating_sp text,
                rating_moodys text,
                rating_fitch text,
                spread_duration double precision,
                cs01 numeric(18,2),
                jtd numeric(18,2),
                expected_loss_pct double precision,
                attachment_pct double precision,
                detachment_pct double precision,
                credit_enhancement_pct double precision,
                wal double precision,
                window_start date,
                window_end date,
                next_pay_date date,
                coupon_current double precision,
                coupon_next double precision,
                cpr_1m double precision,
                cpr_3m double precision,
                cpr_12m double precision,
                cdr_1m double precision,
                cdr_3m double precision,
                cdr_12m double precision,
                severity_3m double precision,
                severity_12m double precision,
                dq_30 double precision,
                dq_60 double precision,
                dq_90plus double precision,
                foreclosure_pct double precision,
                reo_pct double precision,
                wac double precision,
                wala double precision,
                ltv_wavg double precision,
                fico_wavg double precision,
                loan_count integer,
                oc_test_cushion double precision,
                ic_test_cushion double precision,
                warf double precision,
                diversity_score double precision,
                ccc_bucket_pct double precision,
                pnl_carry numeric(18,2),
                pnl_roll_down numeric(18,2),
                pnl_rates numeric(18,2),
                pnl_spread numeric(18,2),
                pnl_idiosyncratic numeric(18,2),
                pnl_fx numeric(18,2),
                pnl_residual numeric(18,2),
                pnl_total_mtd numeric(18,2),
                scn_rm200_sm100 double precision,
                scn_rm200_sm50 double precision,
                scn_rm200_sm25 double precision,
                scn_rm200_sp0 double precision,
                scn_rm200_sp25 double precision,
                scn_rm200_sp50 double precision,
                scn_rm200_sp100 double precision,
                scn_rm200_sp200 double precision,
                scn_rm200_sp300 double precision,
                scn_rm200_sp500 double precision,
                scn_rm100_sm100 double precision,
                scn_rm100_sm50 double precision,
                scn_rm100_sm25 double precision,
                scn_rm100_sp0 double precision,
                scn_rm100_sp25 double precision,
                scn_rm100_sp50 double precision,
                scn_rm100_sp100 double precision,
                scn_rm100_sp200 double precision,
                scn_rm100_sp300 double precision,
                scn_rm100_sp500 double precision,
                scn_rm50_sm100 double precision,
                scn_rm50_sm50 double precision,
                scn_rm50_sm25 double precision,
                scn_rm50_sp0 double precision,
                scn_rm50_sp25 double precision,
                scn_rm50_sp50 double precision,
                scn_rm50_sp100 double precision,
                scn_rm50_sp200 double precision,
                scn_rm50_sp300 double precision,
                scn_rm50_sp500 double precision,
                scn_rm25_sm100 double precision,
                scn_rm25_sm50 double precision,
                scn_rm25_sm25 double precision,
                scn_rm25_sp0 double precision,
                scn_rm25_sp25 double precision,
                scn_rm25_sp50 double precision,
                scn_rm25_sp100 double precision,
                scn_rm25_sp200 double precision,
                scn_rm25_sp300 double precision,
                scn_rm25_sp500 double precision,
                scn_rp0_sm100 double precision,
                scn_rp0_sm50 double precision,
                scn_rp0_sm25 double precision,
                scn_rp0_sp0 double precision,
                scn_rp0_sp25 double precision,
                scn_rp0_sp50 double precision,
                scn_rp0_sp100 double precision,
                scn_rp0_sp200 double precision,
                scn_rp0_sp300 double precision,
                scn_rp0_sp500 double precision,
                scn_rp25_sm100 double precision,
                scn_rp25_sm50 double precision,
                scn_rp25_sm25 double precision,
                scn_rp25_sp0 double precision,
                scn_rp25_sp25 double precision,
                scn_rp25_sp50 double precision,
                scn_rp25_sp100 double precision,
                scn_rp25_sp200 double precision,
                scn_rp25_sp300 double precision,
                scn_rp25_sp500 double precision,
                scn_rp50_sm100 double precision,
                scn_rp50_sm50 double precision,
                scn_rp50_sm25 double precision,
                scn_rp50_sp0 double precision,
                scn_rp50_sp25 double precision,
                scn_rp50_sp50 double precision,
                scn_rp50_sp100 double precision,
                scn_rp50_sp200 double precision,
                scn_rp50_sp300 double precision,
                scn_rp50_sp500 double precision,
                scn_rp100_sm100 double precision,
                scn_rp100_sm50 double precision,
                scn_rp100_sm25 double precision,
                scn_rp100_sp0 double precision,
                scn_rp100_sp25 double precision,
                scn_rp100_sp50 double precision,
                scn_rp100_sp100 double precision,
                scn_rp100_sp200 double precision,
                scn_rp100_sp300 double precision,
                scn_rp100_sp500 double precision,
                scn_rp200_sm100 double precision,
                scn_rp200_sm50 double precision,
                scn_rp200_sm25 double precision,
                scn_rp200_sp0 double precision,
                scn_rp200_sp25 double precision,
                scn_rp200_sp50 double precision,
                scn_rp200_sp100 double precision,
                scn_rp200_sp200 double precision,
                scn_rp200_sp300 double precision,
                scn_rp200_sp500 double precision,
                scn_rp300_sm100 double precision,
                scn_rp300_sm50 double precision,
                scn_rp300_sm25 double precision,
                scn_rp300_sp0 double precision,
                scn_rp300_sp25 double precision,
                scn_rp300_sp50 double precision,
                scn_rp300_sp100 double precision,
                scn_rp300_sp200 double precision,
                scn_rp300_sp300 double precision,
                scn_rp300_sp500 double precision,
                worst_case_price double precision,
                best_case_price double precision,
                scenario_range double precision,
                stress_loss_mv numeric(18,2),
                watchlist_flag boolean,
                restricted_flag boolean,
                comment_count integer,
                last_trade_date date,
                analyst text,
                PRIMARY KEY (as_of_date, position_id)
            );
            -- Grid reads are always (as_of_date, portfolio_id IN (...)); the PK covers position_id paging.
            CREATE INDEX ix_snapshot_portfolio ON core.position_snapshot (as_of_date, portfolio_id) INCLUDE (position_id);

            CREATE TABLE core.position_history (
                as_of_date date NOT NULL, position_id bigint NOT NULL, market_value numeric(18,2) NOT NULL,
                face numeric(18,2) NOT NULL, price double precision NOT NULL, spread_bp double precision NOT NULL,
                dv01 numeric(18,2) NOT NULL, cs01 numeric(18,2) NOT NULL, wal double precision NOT NULL,
                pnl_mtd numeric(18,2) NOT NULL, PRIMARY KEY (position_id, as_of_date));
            CREATE TABLE core.trade (
                trade_id bigint PRIMARY KEY, bond_id integer NOT NULL, portfolio_id integer NOT NULL,
                trade_ts timestamptz NOT NULL, side char(1) NOT NULL CHECK (side IN ('B', 'S')),
                face numeric(18,2) NOT NULL, price double precision NOT NULL, counterparty_id integer NOT NULL, trader text NOT NULL);
            CREATE INDEX ix_trade_ts ON core.trade (trade_ts);
            CREATE INDEX ix_trade_portfolio_ts ON core.trade (portfolio_id, trade_ts);
            CREATE TABLE core.fund_performance (
                fund_id integer NOT NULL REFERENCES core.fund (fund_id), as_of_month date NOT NULL,
                nav numeric(18,2) NOT NULL, balance numeric(18,2) NOT NULL, irr_itd double precision NOT NULL,
                irr_ytd double precision NOT NULL, net_flows numeric(18,2) NOT NULL, PRIMARY KEY (fund_id, as_of_month));
            -- Cash flows can share a date (window-frame tie cases, README §5.2).
            CREATE TABLE core.fund_flow (
                flow_id bigint PRIMARY KEY, fund_id integer NOT NULL REFERENCES core.fund (fund_id),
                flow_date date NOT NULL, amount numeric(18,2) NOT NULL);
            CREATE INDEX ix_fund_flow_date ON core.fund_flow (fund_id, flow_date, flow_id);

            CREATE TABLE market.rate_curve (
                as_of_date date NOT NULL, curve text NOT NULL, tenor_months integer NOT NULL, rate double precision NOT NULL,
                PRIMARY KEY (as_of_date, curve, tenor_months));
            CREATE TABLE market.spread_index (
                as_of_date date NOT NULL, sector text NOT NULL, rating text NOT NULL, spread_bp double precision NOT NULL,
                PRIMARY KEY (as_of_date, sector, rating));

            CREATE TABLE surveillance.deal_remit (
                deal_id integer NOT NULL, period date NOT NULL, loan_count integer NOT NULL,
                dq_30 double precision NOT NULL, dq_60 double precision NOT NULL, dq_90plus double precision NOT NULL,
                cpr double precision NOT NULL, cdr double precision NOT NULL, severity double precision NOT NULL,
                wac double precision NOT NULL, wala double precision NOT NULL, ltv double precision NOT NULL,
                fico double precision NOT NULL, oc_cushion double precision NULL, ic_cushion double precision NULL,
                warf double precision NULL, PRIMARY KEY (deal_id, period));

            CREATE TABLE pricing.vendor_mark (
                as_of_date date NOT NULL, bond_id integer NOT NULL, vendor text NOT NULL, price double precision NOT NULL,
                PRIMARY KEY (as_of_date, bond_id, vendor));
            CREATE TABLE pricing.internal_mark (
                as_of_date date NOT NULL, bond_id integer NOT NULL, price double precision NOT NULL, challenged boolean NOT NULL,
                PRIMARY KEY (as_of_date, bond_id));
            """;

        internal const string DropSchemasSql = """
            DROP SCHEMA IF EXISTS pricing CASCADE;
            DROP SCHEMA IF EXISTS surveillance CASCADE;
            DROP SCHEMA IF EXISTS market CASCADE;
            DROP SCHEMA IF EXISTS core CASCADE;
            DROP SCHEMA IF EXISTS reference CASCADE;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "column_catalog",
                schema: "app",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    group_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    aggregation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    header = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_column_catalog", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_column_catalog_ordinal",
                schema: "app",
                table: "column_catalog",
                column: "ordinal",
                unique: true);

            migrationBuilder.Sql(SchemasSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropSchemasSql);

            migrationBuilder.DropTable(
                name: "column_catalog",
                schema: "app");
        }
    }
}
