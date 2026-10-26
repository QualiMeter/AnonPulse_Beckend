START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    CREATE TABLE poll_knowledge_grading_jobs (
        id uuid NOT NULL,
        poll_id uuid NOT NULL,
        question_id uuid NOT NULL,
        status character varying(16) NOT NULL,
        provider character varying(32) NOT NULL,
        model character varying(128) NOT NULL,
        algorithm_version character varying(32) NOT NULL,
        snapshot_fingerprint character varying(64) NOT NULL,
        expected_answer_snapshot text NOT NULL,
        grading_criteria_snapshot text NOT NULL,
        max_score_snapshot integer NOT NULL,
        answers_snapshot jsonb NOT NULL,
        answer_count integer NOT NULL,
        suggested_count integer NOT NULL,
        reserved_input_tokens integer NOT NULL,
        reserved_output_tokens integer NOT NULL,
        prompt_tokens integer,
        completion_tokens integer,
        created_at timestamp with time zone NOT NULL,
        started_at timestamp with time zone,
        completed_at timestamp with time zone,
        next_attempt_at timestamp with time zone,
        attempts integer NOT NULL,
        error_code character varying(64),
        CONSTRAINT "PK_poll_knowledge_grading_jobs" PRIMARY KEY (id),
        CONSTRAINT ck_knowledge_grading_jobs_attempts CHECK (attempts >= 0),
        CONSTRAINT ck_knowledge_grading_jobs_budget CHECK (reserved_input_tokens > 0 AND reserved_output_tokens > 0),
        CONSTRAINT ck_knowledge_grading_jobs_counts CHECK (answer_count > 0 AND suggested_count >= 0 AND suggested_count <= answer_count),
        CONSTRAINT ck_knowledge_grading_jobs_score CHECK (max_score_snapshot BETWEEN 1 AND 100),
        CONSTRAINT ck_knowledge_grading_jobs_status CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
        CONSTRAINT "FK_poll_knowledge_grading_jobs_poll_questions_question_id" FOREIGN KEY (question_id) REFERENCES poll_questions (id) ON DELETE CASCADE,
        CONSTRAINT "FK_poll_knowledge_grading_jobs_polls_poll_id" FOREIGN KEY (poll_id) REFERENCES polls (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    CREATE TABLE poll_knowledge_grading_suggestions (
        id uuid NOT NULL,
        job_id uuid NOT NULL,
        answer_id uuid NOT NULL,
        suggested_score integer NOT NULL,
        max_score integer NOT NULL,
        explanation text NOT NULL,
        correct_points jsonb NOT NULL,
        missing_or_incorrect_points jsonb NOT NULL,
        needs_review boolean NOT NULL,
        review_reasons jsonb NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_poll_knowledge_grading_suggestions" PRIMARY KEY (id),
        CONSTRAINT ck_knowledge_grading_suggestions_explanation CHECK (btrim(explanation) <> ''),
        CONSTRAINT ck_knowledge_grading_suggestions_score CHECK (max_score BETWEEN 1 AND 100 AND suggested_score BETWEEN 0 AND max_score),
        CONSTRAINT "FK_poll_knowledge_grading_suggestions_named_attempt_answers_an~" FOREIGN KEY (answer_id) REFERENCES named_attempt_answers (id) ON DELETE CASCADE,
        CONSTRAINT "FK_poll_knowledge_grading_suggestions_poll_knowledge_grading_j~" FOREIGN KEY (job_id) REFERENCES poll_knowledge_grading_jobs (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    CREATE INDEX ix_knowledge_grading_queue ON poll_knowledge_grading_jobs (status, next_attempt_at, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    CREATE INDEX "IX_poll_knowledge_grading_jobs_poll_id" ON poll_knowledge_grading_jobs (poll_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    CREATE UNIQUE INDEX ux_knowledge_grading_question_fingerprint ON poll_knowledge_grading_jobs (question_id, snapshot_fingerprint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    CREATE INDEX "IX_poll_knowledge_grading_suggestions_answer_id" ON poll_knowledge_grading_suggestions (answer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    CREATE UNIQUE INDEX ux_knowledge_grading_suggestions_job_answer ON poll_knowledge_grading_suggestions (job_id, answer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026081034_AddKnowledgeGrading') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026081034_AddKnowledgeGrading', '10.0.12');
    END IF;
END $EF$;
COMMIT;
