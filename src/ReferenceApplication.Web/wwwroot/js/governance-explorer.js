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
    const hasPolicySimulator = explorer.dataset.policySimulator === "true";
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

    const sendGovernedAction = async (action, fields, report = setStatus) => {
        if (!requestToken) {
            report("The antiforgery token is unavailable. Reload the page and try again.");
            return;
        }

        const formData = new FormData();
        formData.append("__RequestVerificationToken", requestToken.value);
        Object.entries(fields).forEach(([name, value]) => {
            formData.append(name, value);
        });

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
        report(`Running ${action}…`);

        try {
            const response = await fetch(`/api/v1/governed-operations/${action}`, {
                method: "POST",
                body: formData,
                headers: { "Accept": "application/json" },
                credentials: "same-origin"
            });
            const summary = await readResponseSummary(response);
            report(`${action}: ${summary} (HTTP ${response.status}).`);
            if (!canViewEvidence) {
                awaitingDecision = false;
            }
        } catch {
            awaitingDecision = false;
            report(`${action} could not be requested.`);
        } finally {
            setActionsDisabled(false);
        }
    };

    const runAction = async (button) => {
        if (button.dataset.requiresAcknowledgment === "true" && !acknowledgment?.checked) {
            setStatus("Select the acknowledgment checkbox before requesting the consequential action.");
            acknowledgment?.focus();
            return;
        }

        const fields = {};
        if (button.dataset.includesTitle === "true") {
            fields.Title = recordTitle?.value || "Governance explorer record";
        }

        await sendGovernedAction(button.dataset.governedAction, fields);
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

    const tabs = Array.from(document.querySelectorAll(".explorer-tabs [role='tab']"));
    const outputViews = Array.from(document.querySelectorAll("[data-output-view]"));
    const tabStorageKey = "governance-explorer:tab";

    const showOutput = (view) => {
        const target = outputViews.some((output) => output.dataset.outputView === view) ? view : "evidence";
        outputViews.forEach((output) => {
            output.hidden = output.dataset.outputView !== target;
        });
    };

    const selectTab = (tab, moveFocus) => {
        tabs.forEach((candidate) => {
            const selected = candidate === tab;
            candidate.setAttribute("aria-selected", selected ? "true" : "false");
            candidate.tabIndex = selected ? 0 : -1;
            const panel = document.getElementById(candidate.getAttribute("aria-controls"));
            if (panel) {
                panel.hidden = !selected;
            }
        });

        showOutput(tab.dataset.output);
        if (moveFocus) {
            tab.focus();
        }

        try {
            window.sessionStorage.setItem(tabStorageKey, tab.id);
        } catch {
            // Remembering the tab is a convenience; storage can be unavailable.
        }
    };

    tabs.forEach((tab, index) => {
        tab.addEventListener("click", () => selectTab(tab, false));
        tab.addEventListener("keydown", (event) => {
            let next = null;
            if (event.key === "ArrowRight" || event.key === "ArrowDown") {
                next = tabs[(index + 1) % tabs.length];
            } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
                next = tabs[(index - 1 + tabs.length) % tabs.length];
            } else if (event.key === "Home") {
                next = tabs[0];
            } else if (event.key === "End") {
                next = tabs[tabs.length - 1];
            }

            if (next) {
                event.preventDefault();
                selectTab(next, true);
            }
        });
    });

    if (tabs.length > 0) {
        let initialTab = tabs[0];
        if (!databaseWasReset) {
            try {
                const rememberedTab = document.getElementById(window.sessionStorage.getItem(tabStorageKey) || "");
                if (rememberedTab && tabs.includes(rememberedTab)) {
                    initialTab = rememberedTab;
                }
            } catch {
                // Fall back to the first tab when storage is unavailable.
            }
        }

        selectTab(initialTab, false);
    }

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

    if (hasPolicySimulator) {
        const simulatorForm = document.getElementById("policy-simulator-form");
        const evaluateButton = document.getElementById("evaluate-policy-simulation");
        const sendButton = document.getElementById("send-policy-simulation");
        const simulatorStatus = document.getElementById("policy-simulator-status");
        const simulatorResult = document.getElementById("policy-simulator-result");
        const simulatorEmpty = document.getElementById("policy-simulator-empty");

        if (simulatorForm && evaluateButton && sendButton && simulatorStatus && simulatorResult) {
            let lastSimulation = null;

            const formatCodes = (codes) => (codes && codes.length > 0 ? codes.join(", ") : "none");
            const formatTime = (value) => (value ? new Date(value).toLocaleString() : null);

            const createStage = (number, title) => {
                const item = document.createElement("li");
                item.className = "simulation-stage";
                const heading = document.createElement("h3");
                heading.textContent = `${number}. ${title}`;
                item.append(heading);
                return item;
            };

            const createDetails = (rows) => {
                const details = document.createElement("dl");
                rows.forEach(([label, value]) => appendDetail(details, label, value));
                return details;
            };

            const renderConstraints = (result) => {
                const stage = createStage(1, "Constraints, in evaluation order");
                const list = document.createElement("ol");
                list.className = "simulation-constraints";
                result.constraints.forEach((constraint) => {
                    const item = document.createElement("li");
                    const outcome = constraint.disabled
                        ? "left out of the simulated policy"
                        : constraint.evaluated ? constraint.outcome : "not evaluated";
                    const reasons = constraint.reasonCodes.length > 0 ? ` (${constraint.reasonCodes.join(", ")})` : "";
                    item.textContent = `${constraint.name}: ${outcome}${reasons}`;
                    if (constraint.disabled) {
                        item.className = "disabled-constraint";
                    }
                    list.append(item);
                });
                stage.append(list);
                return stage;
            };

            const renderDecisionPolicy = (result) => {
                const stage = createStage(2, "Composed decision and decision policy");
                const policy = result.decisionPolicy;
                stage.append(createDetails([
                    ["Composed", policy.composedOutcome],
                    ["Composed reasons", formatCodes(policy.composedReasonCodes)],
                    ["After the decision policy", policy.outcome],
                    ["Reasons", formatCodes(policy.reasonCodes)],
                    ["Changed by the policy", policy.changed ? "yes" : "no"]
                ]));
                return stage;
            };

            const renderCapability = (result) => {
                const stage = createStage(3, "Capability grant");
                const capability = result.capability;
                if (!capability.applies) {
                    const note = document.createElement("p");
                    note.textContent = "The endpoint declares no capability scopes, so this stage does not apply.";
                    stage.append(note);
                    return stage;
                }

                stage.append(createDetails([
                    ["Model", capability.model === "signed-token" ? "Signed capability token" : "Permission claims"],
                    ["Required scopes", capability.requiredScopes],
                    ["Validated", capability.evaluated ? "yes" : "no: the decision was already denied"],
                    ["Before", capability.inputOutcome],
                    ["After", capability.outcome],
                    ["Reasons", capability.evaluated ? formatCodes(capability.reasonCodes) : null]
                ]));

                const token = capability.token;
                if (token) {
                    const heading = document.createElement("h4");
                    heading.textContent = token.wouldBeIssued
                        ? "Token that would be issued (never signed, no use consumed)"
                        : "No token would be issued: the actor lacks the capability claim";
                    stage.append(heading, createDetails([
                        ["Token", token.tokenId],
                        ["Issuer", token.issuer],
                        ["Audience", token.audience],
                        ["Subject", token.subjectId],
                        ["Operation", token.operationName],
                        ["Scopes", token.scopes],
                        ["Payload binding", token.resourceBinding],
                        ["Issued", formatTime(token.issuedUtc)],
                        ["Not before", formatTime(token.notBeforeUtc)],
                        ["Expires", formatTime(token.expiresUtc)],
                        ["Use limit", token.maxUseCount],
                        ["Policy version", token.policyVersion],
                        ["Policy hash", token.policyHash],
                        ["Signing key", token.keyId ? `${token.keyId} version ${token.keyVersion} (key not shown)` : null],
                        ["Signature algorithm", token.signatureAlgorithm]
                    ]));
                }

                return stage;
            };

            const renderReceipt = (result) => {
                const stage = createStage(4, "Decision receipt that would be appended");
                const receipt = result.receipt;
                if (!receipt) {
                    const note = document.createElement("p");
                    note.textContent = "No decision receipt would be written for this request.";
                    stage.append(note);
                    return stage;
                }

                stage.append(createDetails([
                    ["Receipt", receipt.decisionReceiptId],
                    ["Operation", receipt.operationName],
                    ["Outcome", receipt.outcome],
                    ["Reasons", formatCodes(receipt.reasonCodes)],
                    ["Actor", receipt.actorDisplayName ? `${receipt.actorDisplayName} (${receipt.actorId})` : receipt.actorId],
                    ["Actor type", receipt.actorType],
                    ["Policy version", receipt.policyVersion],
                    ["Policy hash", receipt.policyHash],
                    ["Decision stage", receipt.decisionStage],
                    ["Capability grant", receipt.capabilityGrantId],
                    ["Correlation", receipt.correlationId]
                ]));
                return stage;
            };

            const renderEndpointResult = (result) => {
                const endpointResult = result.endpointResult;
                const summary = document.createElement("p");
                summary.className = `simulation-endpoint-result simulation-${endpointResult.kind}`;
                const status = endpointResult.statusCode ? ` (HTTP ${endpointResult.statusCode})` : "";
                summary.textContent = `${endpointResult.description}${status}`;
                const elements = [summary];
                if (endpointResult.requiredAcknowledgmentCode) {
                    elements.push(createDetails([
                        ["Acknowledgment code", endpointResult.requiredAcknowledgmentCode],
                        ["Acknowledgment text", endpointResult.requiredAcknowledgmentText]
                    ]));
                }

                return elements;
            };

            const renderSimulation = (result) => {
                const children = [];
                if (result.simulatedWithModifiedPolicy) {
                    const banner = document.createElement("p");
                    banner.className = "modified-policy-label";
                    banner.setAttribute("role", "note");
                    banner.textContent =
                        `Simulated with a modified policy: ${result.disabledConstraints.join(", ")} left out.`;
                    children.push(banner);
                }

                const heading = document.createElement("h3");
                heading.textContent = `Simulated ${result.operationName}`;
                const stages = document.createElement("ol");
                stages.className = "simulation-stages";
                stages.append(
                    renderConstraints(result),
                    renderDecisionPolicy(result),
                    renderCapability(result),
                    renderReceipt(result));
                children.push(heading, ...renderEndpointResult(result), stages);
                simulatorResult.replaceChildren(...children);
                if (simulatorEmpty) {
                    simulatorEmpty.hidden = true;
                }
            };

            const clearSimulation = (message) => {
                simulatorResult.replaceChildren();
                if (simulatorEmpty) {
                    simulatorEmpty.hidden = false;
                }

                simulatorStatus.textContent = message;
            };

            const resetSend = () => {
                lastSimulation = null;
                sendButton.disabled = true;
            };

            simulatorForm.addEventListener("change", resetSend);
            simulatorForm.addEventListener("input", resetSend);

            simulatorForm.addEventListener("submit", async (event) => {
                event.preventDefault();
                const body = new FormData(simulatorForm);
                simulatorForm.querySelectorAll("input[data-constraint-name]").forEach((input) => {
                    if (!input.checked) {
                        body.append("disabledConstraints", input.dataset.constraintName);
                    }
                });

                resetSend();
                showOutput("simulator");
                evaluateButton.disabled = true;
                simulatorStatus.textContent = "Evaluating the simulated request…";
                try {
                    const response = await fetch("/api/v1/policy-simulator/evaluate", {
                        method: "POST",
                        body,
                        headers: { "Accept": "application/json" },
                        credentials: "same-origin"
                    });
                    const contentType = response.headers.get("content-type") || "";
                    const payload = contentType.includes("json") ? await response.json() : null;
                    if (!response.ok || !payload) {
                        clearSimulation(payload?.message || `The simulation returned HTTP ${response.status}.`);
                        return;
                    }

                    lastSimulation = payload;
                    renderSimulation(payload);
                    sendButton.disabled = !payload.sendForReal.available;
                    simulatorStatus.textContent = payload.sendForReal.available
                        ? "Simulated; nothing was persisted. Send it for real to compare the receipt with the persisted decision."
                        : `Simulated; nothing was persisted. ${payload.sendForReal.reason}`;
                } catch {
                    clearSimulation("The simulation could not be requested.");
                } finally {
                    evaluateButton.disabled = false;
                }
            });

            sendButton.addEventListener("click", () => {
                if (!lastSimulation || !lastSimulation.sendForReal.available) {
                    return;
                }

                const simulation = lastSimulation;
                sendButton.disabled = true;
                showOutput("evidence");
                const reportSend = (message) => {
                    simulatorStatus.textContent = canViewEvidence
                        ? `Sent through the real endpoint. ${message} The evidence timeline on the right shows the persisted decision; select this tab again to return to the simulated receipt.`
                        : `Sent through the real endpoint. ${message}`;
                };
                void sendGovernedAction(simulation.operation, simulation.payload, reportSend);
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
