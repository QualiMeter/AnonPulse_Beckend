START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempts DROP CONSTRAINT ck_named_attempts_not_final;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempt_answers DROP CONSTRAINT ck_named_attempt_answers_grading_state;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempt_answers DROP CONSTRAINT ck_named_attempt_answers_score;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempts ADD finalized_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempts ADD finalized_by_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempt_answers ADD CONSTRAINT ak_named_attempt_answers_attempt_id_id UNIQUE (attempt_id, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    CREATE TABLE test_grade_changes (
        id uuid NOT NULL,
        attempt_id uuid NOT NULL,
        answer_id uuid NOT NULL,
        actor_id uuid NOT NULL,
        old_score numeric(10,2),
        new_score numeric(10,2) NOT NULL,
        source character varying(16) NOT NULL,
        knowledge_grading_suggestion_id uuid,
        changed_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_test_grade_changes" PRIMARY KEY (id),
        CONSTRAINT ck_test_grade_changes_scores CHECK ((old_score IS NULL OR old_score >= 0) AND new_score >= 0),
        CONSTRAINT ck_test_grade_changes_source CHECK (source IN ('manual', 'ai_suggestion') AND ((source = 'manual' AND knowledge_grading_suggestion_id IS NULL) OR (source = 'ai_suggestion' AND knowledge_grading_suggestion_id IS NOT NULL))),
        CONSTRAINT "FK_test_grade_changes_named_attempt_answers_attempt_id_answer_~" FOREIGN KEY (attempt_id, answer_id) REFERENCES named_attempt_answers (attempt_id, id) ON DELETE CASCADE,
        CONSTRAINT "FK_test_grade_changes_named_attempts_attempt_id" FOREIGN KEY (attempt_id) REFERENCES named_attempts (id) ON DELETE CASCADE,
        CONSTRAINT "FK_test_grade_changes_poll_knowledge_grading_suggestions_knowl~" FOREIGN KEY (knowledge_grading_suggestion_id) REFERENCES poll_knowledge_grading_suggestions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempts ADD CONSTRAINT ck_named_attempts_finalization CHECK ((is_final = false AND finalized_by_user_id IS NULL AND finalized_at IS NULL) OR (is_final = true AND is_fully_graded = true AND normalized_score IS NOT NULL AND finalized_by_user_id IS NOT NULL AND finalized_at IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempt_answers ADD CONSTRAINT ck_named_attempt_answers_grading_state CHECK (grading_state IN ('auto_graded', 'pending', 'teacher_graded'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    ALTER TABLE named_attempt_answers ADD CONSTRAINT ck_named_attempt_answers_score CHECK (maximum_score > 0 AND ((grading_state IN ('auto_graded', 'teacher_graded') AND score IS NOT NULL AND score >= 0 AND score <= maximum_score) OR (grading_state = 'pending' AND score IS NULL)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    CREATE INDEX ix_test_grade_changes_answer_id ON test_grade_changes (answer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    CREATE INDEX ix_test_grade_changes_attempt_changed_at ON test_grade_changes (attempt_id, changed_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    CREATE INDEX "IX_test_grade_changes_attempt_id_answer_id" ON test_grade_changes (attempt_id, answer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    CREATE INDEX ix_test_grade_changes_suggestion_id ON test_grade_changes (knowledge_grading_suggestion_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027031338_FinalizeTestGrades') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261027031338_FinalizeTestGrades', '10.0.14');
    END IF;
END $EF$;
COMMIT;
