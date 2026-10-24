START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261023064551_AddAdministrationAndAudit') THEN
    ALTER TABLE users ADD is_administratively_deactivated boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261023064551_AddAdministrationAndAudit') THEN
    CREATE TABLE admin_audit_events (
        id uuid NOT NULL,
        administrator_id uuid NOT NULL,
        action_type character varying(64) NOT NULL,
        target_type character varying(32) NOT NULL,
        target_id uuid,
        occurred_at timestamp with time zone NOT NULL,
        metadata jsonb NOT NULL,
        CONSTRAINT "PK_admin_audit_events" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261023064551_AddAdministrationAndAudit') THEN
    CREATE INDEX ix_admin_audit_events_administrator_id ON admin_audit_events (administrator_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261023064551_AddAdministrationAndAudit') THEN
    CREATE INDEX ix_admin_audit_events_occurred_at ON admin_audit_events (occurred_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261023064551_AddAdministrationAndAudit') THEN
    CREATE INDEX ix_admin_audit_events_target ON admin_audit_events (target_type, target_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261023064551_AddAdministrationAndAudit') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261023064551_AddAdministrationAndAudit', '10.0.12');
    END IF;
END $EF$;
COMMIT;

