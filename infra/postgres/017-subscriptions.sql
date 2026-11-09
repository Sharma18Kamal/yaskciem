CREATE TABLE IF NOT EXISTS CiemSubscriptions (
    TenantId uuid PRIMARY KEY,
    TrialStartedAtUtc timestamptz NOT NULL,
    TrialExpiresAtUtc timestamptz NOT NULL,
    LicensedUntilUtc timestamptz NULL,
    CHECK (TrialExpiresAtUtc = TrialStartedAtUtc + interval '720 hours')
);

REVOKE ALL ON CiemSubscriptions FROM PUBLIC;
GRANT SELECT ON CiemSubscriptions TO yask_ciem_runtime;
GRANT INSERT (TenantId, TrialStartedAtUtc, TrialExpiresAtUtc) ON CiemSubscriptions TO yask_ciem_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON CiemSubscriptions FROM yask_ciem_runtime;
