(() => {
    "use strict";

    const explorer = document.querySelector(".explorer-grid");
    const actionsForm = document.getElementById("governance-actions");
    const statusRegion = document.getElementById("explorer-status");
    const timeline = document.getElementById("evidence-timeline");
    const correlationLabel = document.getElementById("explorer-correlation");
    const verificationResult = document.getElementById("verification-result");
    const verifyButton = document.getElementById("verify-evidence");

    if (!explorer || !actionsForm || !statusRegion || !timeline || !correlationLabel ||
        !verificationResult || !verifyButton) {
        return;
    }

    const canViewEvidence = explorer.dataset.canViewEvidence === "true";
    const databaseWasReset = explorer.dataset.databaseReset === "true";
    const hasLoadDemonstration = explorer.dataset.loadDemonstration === "true";
    const actionButtons = Array.from(actionsForm.querySelectorAll("[data-governed-action]"));
    const requestToken = actionsForm.querySelector("input[name='__RequestVerificationToken']");
    const acknowledgment = document.getElementById("explorer-acknowledgment");
    const recordTitle = document.getElementById("explorer-record-title");
    let activeCorrelationId = null;
    let awaitingDecision = false;

    const setStatus = (message) => {
        statusRegion.textContent = message;
    };

    const setActionsDisabled = (disabled) => {
        actionButtons.forEach((button) => {
            button.disabled = disabled;
        });
    };

    const appendDetail = (list, label, value) => {
        if (value === null || value === undefined || value === "" ||
            (Array.isArray(value) && value.length === 0)) {
            return;
        }

        const term = document.createElement("dt");
        term.textContent = label;
        const description = document.createElement("dd");
        description.textContent = Array.isArray(value) ? value.join(", ") : String(value);
        list.append(term, description);
    };

    const renderTimeline = (response) => {
        timeline.replaceChildren();
        response.entries.forEach((entry) => {
            const item = document.createElement("li");
            item.className = "evidence-entry";
            const heading = document.createElement("h3");
            heading.textContent = entry.kind.replaceAll("-", " ");
            const details = document.createElement("dl");
            appendDetail(details, "Occurred", new Date(entry.occurredUtc).toLocaleString());
            appendDetail(details, "Outcome", entry.outcome);
            appendDetail(details, "Status", entry.status);
            appendDetail(details, "Operation", entry.operationName);
            appendDetail(details, "Reasons", entry.reasonCodes);
            appendDetail(details, "Policy version", entry.policyVersion);
            appendDetail(details, "Policy hash", entry.policyHash);
            appendDetail(details, "Decision record", entry.decisionAuditRecordId);
            appendDetail(details, "Mutation batch", entry.mutationBatchId);
            appendDetail(details, "Execution", entry.operationExecutionId);
            appendDetail(details, "Evidence hash", entry.evidenceHash);
            item.append(heading, details);
            timeline.append(item);
        });

        if (response.entries.length === 0) {
            const empty = document.createElement("li");
            empty.className = "empty-evidence";
            empty.textContent = "No persisted evidence was returned for this request.";
            timeline.append(empty);
        }
    };

    const loadEvidence = async () => {
        if (!canViewEvidence || !activeCorrelationId) {
            return;
        }

        timeline.setAttribute("aria-busy", "true");
        try {
            const response = await fetch(
                `/api/v1/governance-evidence/${encodeURIComponent(activeCorrelationId)}`,
                { headers: { "Accept": "application/json" }, credentials: "same-origin" });
            if (!response.ok) {
                throw new Error(`Evidence query returned HTTP ${response.status}.`);
            }

            renderTimeline(await response.json());
        } catch (error) {
            setStatus(error instanceof Error ? error.message : "Evidence could not be loaded.");
        } finally {
            timeline.setAttribute("aria-busy", "false");
        }
    };

    const readResponseSummary = async (response) => {
        const contentType = response.headers.get("content-type") || "";
        if (contentType.includes("json")) {
            const payload = await response.json();
            return payload.message || payload.title || payload.detail || `HTTP ${response.status}`;
        }

        return `HTTP ${response.status}`;
    };

    const runAction = async (button) => {
        if (!requestToken) {
            setStatus("The antiforgery token is unavailable. Reload the page and try again.");
            return;
        }

        if (button.dataset.requiresAcknowledgment === "true" && !acknowledgment?.checked) {
            setStatus("Select the acknowledgment checkbox before requesting the consequential action.");
            acknowledgment?.focus();
            return;
        }

        const action = button.dataset.governedAction;
        const formData = new FormData();
        formData.append("__RequestVerificationToken", requestToken.value);
        if (button.dataset.includesTitle === "true") {
            formData.append("Title", recordTitle?.value || "Governance explorer record");
        }

        awaitingDecision = canViewEvidence;
        activeCorrelationId = null;
        verifyButton.disabled = true;
        verificationResult.replaceChildren();
        const waiting = document.createElement("li");
        waiting.className = "empty-evidence";
        waiting.textContent = canViewEvidence
            ? "Waiting for persisted decision evidence…"
            : "Full evidence requires an administrator role.";
        timeline.replaceChildren(waiting);
        correlationLabel.textContent = canViewEvidence
            ? "Waiting for the persisted decision notification…"
            : "Full evidence requires an administrator role.";
        setActionsDisabled(true);
        setStatus(`Running ${action}…`);

        try {
            const response = await fetch(`/api/v1/governed-operations/${action}`, {
                method: "POST",
                body: formData,
                headers: { "Accept": "application/json" },
                credentials: "same-origin"
            });
            const summary = await readResponseSummary(response);
            setStatus(`${action}: ${summary} (HTTP ${response.status}).`);
            if (!canViewEvidence) {
                awaitingDecision = false;
            }
        } catch {
            awaitingDecision = false;
            setStatus(`${action} could not be requested.`);
        } finally {
            setActionsDisabled(false);
        }
    };

    actionButtons.forEach((button) => {
        button.addEventListener("click", () => runAction(button));
    });

    verifyButton.addEventListener("click", async () => {
        if (!activeCorrelationId) {
            return;
        }

        verifyButton.disabled = true;
        verificationResult.textContent = "Verifying canonical record hashes…";
        try {
            const response = await fetch(
                `/api/v1/governance-evidence/${encodeURIComponent(activeCorrelationId)}/verification`,
                { headers: { "Accept": "application/json" }, credentials: "same-origin" });
            if (!response.ok) {
                throw new Error(`Verification returned HTTP ${response.status}.`);
            }

            const payload = await response.json();
            const list = document.createElement("ul");
            payload.records.forEach((record) => {
                const item = document.createElement("li");
                item.textContent = `${record.recordId}: ${record.status}`;
                list.append(item);
            });
            verificationResult.replaceChildren(list);
        } catch (error) {
            verificationResult.textContent = error instanceof Error
                ? error.message
                : "Evidence verification failed.";
        } finally {
            verifyButton.disabled = false;
        }
    });

    if (hasLoadDemonstration) {
        const form = document.getElementById("load-demonstration-form");
        const startButton = document.getElementById("start-load-demonstration");
        const cancelButton = document.getElementById("cancel-load-demonstration");
        const loadStatus = document.getElementById("load-demonstration-status");
        const progress = document.getElementById("load-demonstration-progress");
        const elapsed = document.getElementById("load-demonstration-elapsed");
        const throughput = document.getElementById("load-demonstration-throughput");
        const histogram = document.getElementById("load-demonstration-histogram");

        if (form && startButton && cancelButton && loadStatus && progress && elapsed && throughput && histogram) {
            const renderLoadSnapshot = (snapshot) => {
                const running = snapshot.state === "running";
                startButton.disabled = running;
                cancelButton.disabled = !running;
                loadStatus.textContent = snapshot.message;
                progress.textContent = `${snapshot.completedIterations} / ${snapshot.targetIterations}`;
                elapsed.textContent = `${snapshot.elapsedMilliseconds.toFixed(1)} ms`;
                throughput.textContent = `${snapshot.throughputPerSecond.toFixed(1)} decisions/s`;
                const buckets = snapshot.latencyHistogram.map((bucket) => {
                    const item = document.createElement("li");
                    item.textContent = `${bucket.label}: ${bucket.count}`;
                    return item;
                });
                histogram.replaceChildren(...buckets);
            };

            const postLoadAction = async (path, body) => {
                try {
                    const response = await fetch(path, {
                        method: "POST",
                        body,
                        headers: { "Accept": "application/json" },
                        credentials: "same-origin"
                    });
                    const payload = await response.json();
                    if (!response.ok) {
                        loadStatus.textContent = payload.message || `Request returned HTTP ${response.status}.`;
                    }
                } catch {
                    loadStatus.textContent = "The demonstration request could not be completed.";
                }
            };

            form.addEventListener("submit", (event) => {
                event.preventDefault();
                startButton.disabled = true;
                void postLoadAction(
                    "/api/v1/governance-load-demonstration/start",
                    new FormData(form));
            });
            cancelButton.addEventListener("click", () => {
                const body = new FormData();
                const token = form.querySelector("input[name='__RequestVerificationToken']");
                if (token) {
                    body.append("__RequestVerificationToken", token.value);
                }
                void postLoadAction("/api/v1/governance-load-demonstration/cancel", body);
            });

            const loadNotifications = new EventSource(
                "/api/v1/governance-load-demonstration/notifications");
            loadNotifications.addEventListener("load-demonstration", (event) => {
                renderLoadSnapshot(JSON.parse(event.data));
            });
            loadNotifications.addEventListener("error", () => {
                startButton.disabled = true;
                cancelButton.disabled = true;
                loadStatus.textContent = "The live demonstration metrics stream is reconnecting.";
            });
        }
    }

    if (canViewEvidence) {
        setActionsDisabled(true);
        setStatus("Connecting to the evidence stream…");
        const notifications = new EventSource("/api/v1/governance-evidence/notifications");
        notifications.addEventListener("open", () => {
            setActionsDisabled(false);
            setStatus(databaseWasReset
                ? "Demonstration database reset. The migrated evidence state is empty."
                : "Evidence stream connected. Ready to run a governed action.");
        });
        notifications.addEventListener("evidence", (event) => {
            const notification = JSON.parse(event.data);
            if (awaitingDecision && notification.kind === "decision") {
                activeCorrelationId = notification.correlationId;
                awaitingDecision = false;
                correlationLabel.textContent = `Correlation: ${activeCorrelationId}`;
                verifyButton.disabled = false;
                void loadEvidence();
                return;
            }

            if (notification.correlationId === activeCorrelationId) {
                void loadEvidence();
                if (notification.kind === "lifecycle-event") {
                    // The lifecycle notification is published inside the outbox destination before the dispatcher
                    // marks the source entry delivered. Re-query once more after that transaction can commit.
                    window.setTimeout(() => void loadEvidence(), 750);
                }
            }
        });
        notifications.addEventListener("error", () => {
            setActionsDisabled(true);
            setStatus("The evidence stream is reconnecting. Persisted evidence remains available.");
        });
    }
})();
