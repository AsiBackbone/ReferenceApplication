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
    const hasDecisionReplay = explorer.dataset.decisionReplay === "true";
    const hasPipelineInspector = explorer.dataset.pipelineInspector === "true";
    const hasFailureScenarios = explorer.dataset.failureScenarios === "true";
    const actionButtons = Array.from(actionsForm.querySelectorAll("[data-governed-action]"));
    const requestToken = actionsForm.querySelector("input[name='__RequestVerificationToken']");
    const acknowledgment = document.getElementById("explorer-acknowledgment");
    const recordTitle = document.getElementById("explorer-record-title");
    let activeCorrelationId = null;
    // Panels that refresh on evidence-stream hints register here; the stream itself is opened once, below.
    const evidenceHintListeners = [];
    const notifyEvidenceHint = (notification) => {
        evidenceHintListeners.forEach((listener) => listener(notification));
    };
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

    // Stage renderers shared by the policy simulator and decision replay, whose branches carry the same stages.
    const capabilityModels = {
        "claims": "Permission claims",
        "signed-token": "Signed capability token",
        "recorded-grant": "Signed capability token, outcome taken from the record and not verified again"
    };

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
            ["Model", capabilityModels[capability.model] || capability.model],
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

    if (hasPolicySimulator) {
        const simulatorForm = document.getElementById("policy-simulator-form");
        const evaluateButton = document.getElementById("evaluate-policy-simulation");
        const sendButton = document.getElementById("send-policy-simulation");
        const simulatorStatus = document.getElementById("policy-simulator-status");
        const simulatorResult = document.getElementById("policy-simulator-result");
        const simulatorEmpty = document.getElementById("policy-simulator-empty");

        if (simulatorForm && evaluateButton && sendButton && simulatorStatus && simulatorResult) {
            let lastSimulation = null;

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

    if (hasDecisionReplay) {
        const replayPanel = document.getElementById("explorer-panel-replay");
        const replayTab = document.getElementById("explorer-tab-replay");
        const lookupForm = document.getElementById("decision-replay-lookup");
        const receiptInput = document.getElementById("decision-replay-receipt");
        const recentList = document.getElementById("decision-replay-recent");
        const refreshButton = document.getElementById("decision-replay-refresh");
        const olderButton = document.getElementById("decision-replay-older");
        const replayForm = document.getElementById("decision-replay-form");
        const replayButton = document.getElementById("replay-decision");
        const replayStatus = document.getElementById("decision-replay-status");
        const replayEmpty = document.getElementById("decision-replay-empty");
        const replayDetails = document.getElementById("decision-replay-details");
        const replayResult = document.getElementById("decision-replay-result");

        if (replayPanel && lookupForm && receiptInput && recentList && refreshButton && olderButton && replayForm &&
            replayButton && replayStatus && replayDetails && replayResult) {
            const decisionsPath = "/api/v1/decision-replay/decisions";
            const factNames = { administrator: "Administrator role", capabilityGrant: "Capability grant" };
            const inputStatusLabels = { recorded: "Recorded", inferred: "Inferred", unrecoverable: "Not recorded" };
            let selectedReceiptId = null;
            let nextCursor = null;
            let recentLoaded = false;

            const shortHash = (hash) => (hash && hash.length > 20 ? `${hash.slice(0, 19)}…` : hash || "(no hash)");

            const shortOperation = (name) => {
                const withoutAssembly = name.replace(/\s*\([^)]*\)$/, "");
                return withoutAssembly.slice(withoutAssembly.lastIndexOf(".") + 1) || name;
            };

            const getJson = async (url) => {
                const response = await fetch(url, { headers: { "Accept": "application/json" }, credentials: "same-origin" });
                const contentType = response.headers.get("content-type") || "";
                return { response, payload: contentType.includes("json") ? await response.json() : null };
            };

            const markSelected = () => {
                recentList.querySelectorAll("button[data-receipt-id]").forEach((button) => {
                    if (button.dataset.receiptId === selectedReceiptId) {
                        button.setAttribute("aria-current", "true");
                    } else {
                        button.removeAttribute("aria-current");
                    }
                });
            };

            const renderRecent = (decisions, append) => {
                if (!append) {
                    recentList.replaceChildren();
                }

                if (!append && decisions.length === 0) {
                    const empty = document.createElement("li");
                    empty.className = "output-note";
                    empty.textContent = "No decisions are persisted yet. Run a governed action first.";
                    recentList.append(empty);
                    return;
                }

                decisions.forEach((decision) => {
                    const item = document.createElement("li");
                    const button = document.createElement("button");
                    button.type = "button";
                    button.className = "decision-replay-pick";
                    button.dataset.receiptId = decision.decisionReceiptId;
                    const title = document.createElement("span");
                    title.className = "decision-replay-pick-title";
                    title.textContent = `${shortOperation(decision.operationName)}: ${decision.outcome}`;
                    const meta = document.createElement("span");
                    meta.className = "decision-replay-pick-meta";
                    meta.textContent = `${new Date(decision.occurredUtc).toLocaleString()} · ${decision.actorId}` +
                        (decision.policyMatchesCurrent ? "" : " · different policy");
                    button.append(title, meta);
                    button.addEventListener("click", () => void selectDecision(decision.decisionReceiptId));
                    item.append(button);
                    recentList.append(item);
                });
                markSelected();
            };

            const loadRecent = async (append) => {
                const url = append && nextCursor
                    ? `${decisionsPath}?before=${encodeURIComponent(nextCursor)}`
                    : decisionsPath;
                recentList.setAttribute("aria-busy", "true");
                try {
                    const { response, payload } = await getJson(url);
                    if (!response.ok || !payload) {
                        replayStatus.textContent = payload?.message || `The decision list returned HTTP ${response.status}.`;
                        return;
                    }

                    recentLoaded = true;
                    renderRecent(payload.decisions, append);
                    nextCursor = payload.nextCursor;
                    olderButton.hidden = !nextCursor;
                } catch {
                    replayStatus.textContent = "The decision list could not be loaded.";
                } finally {
                    recentList.setAttribute("aria-busy", "false");
                }
            };

            const createNote = (className, text) => {
                const note = document.createElement("p");
                note.className = className;
                note.setAttribute("role", "note");
                note.textContent = text;
                return note;
            };

            const renderDetails = (details) => {
                const children = details.policy.notices.map((notice) => createNote("modified-policy-label", notice));

                const card = document.createElement("section");
                card.className = "replay-card";
                const heading = document.createElement("h3");
                heading.textContent = "Selected decision";
                const evidence = details.evidence;
                card.append(heading, createDetails([
                    ["Receipt", details.decisionReceiptId],
                    ["Occurred", formatTime(details.occurredUtc)],
                    ["Operation", details.operationName],
                    ["Actor", details.actorDisplayName ? `${details.actorDisplayName} (${details.actorId})` : details.actorId],
                    ["Outcome", details.outcome],
                    ["Reasons", formatCodes(details.reasonCodes)],
                    ["Recorded policy", `${details.policy.recordedVersion || "unversioned"} ${details.policy.recordedHash || "(no hash)"}`],
                    ["Current policy", `${details.policy.currentVersion} ${details.policy.currentHash}`],
                    ["Evidence hash", evidence.storedHash ? `${evidence.status}: ${evidence.storedHash}` : evidence.status],
                    ["Capability grant", details.capabilityGrantId]
                ]));
                if (evidence.status === "mismatch") {
                    card.append(createNote(
                        "modified-policy-label",
                        "The stored evidence hash does not match the record. Treat this decision's fields as unverified."));
                }

                const inputsCard = document.createElement("section");
                inputsCard.className = "replay-card";
                const inputsHeading = document.createElement("h3");
                inputsHeading.textContent = "What the ledger can reconstruct";
                const inputs = document.createElement("ul");
                inputs.className = "replay-inputs";
                details.inputs.forEach((input) => {
                    const item = document.createElement("li");
                    const name = document.createElement("strong");
                    name.textContent = input.name;
                    const badge = document.createElement("span");
                    badge.className = `replay-input-status replay-input-${input.status}`;
                    badge.textContent = inputStatusLabels[input.status] || input.status;
                    item.append(name, badge);
                    if (input.value) {
                        const value = document.createElement("code");
                        value.textContent = input.value;
                        item.append(value);
                    }

                    const explanation = document.createElement("p");
                    explanation.textContent = input.explanation;
                    item.append(explanation);
                    inputs.append(item);
                });
                inputsCard.append(inputsHeading, inputs);

                children.push(card, inputsCard);
                if (!details.replayable) {
                    children.push(createNote("replay-partial", details.notReplayableReason));
                }

                replayDetails.replaceChildren(...children);
                if (replayEmpty) {
                    replayEmpty.hidden = true;
                }
            };

            const createReasonList = (codes, added, removed) => {
                const list = document.createElement("ul");
                list.className = "replay-reasons";
                codes.forEach((code) => {
                    const item = document.createElement("li");
                    item.textContent = code;
                    if (added.includes(code)) {
                        item.className = "reason-added";
                        item.title = "Not in the recorded decision";
                    }

                    list.append(item);
                });
                removed.forEach((code) => {
                    const item = document.createElement("li");
                    item.className = "reason-removed";
                    item.title = "In the recorded decision, not in the replay";
                    item.textContent = code;
                    list.append(item);
                });
                if (list.children.length === 0) {
                    const item = document.createElement("li");
                    item.textContent = "none";
                    list.append(item);
                }

                return list;
            };

            const createOutcomeRow = (outcome, changed) => {
                const row = document.createElement("p");
                row.className = changed ? "replay-outcome replay-changed" : "replay-outcome";
                row.textContent = outcome;
                return row;
            };

            const renderBranch = (branch) => {
                const container = document.createElement("div");
                container.className = "replay-branch";
                const assumptions = Object.entries(branch.assumptions);
                if (assumptions.length > 0) {
                    const heading = document.createElement("h4");
                    heading.textContent = `If ${assumptions
                        .map(([key, value]) => `${(factNames[key] || key).toLowerCase()}: ${value ? "yes" : "no"}`)
                        .join(", ")}`;
                    container.append(heading);
                }

                const verdict = document.createElement("p");
                verdict.className = branch.difference.matches ? "replay-verdict replay-matches" : "replay-verdict replay-differs";
                verdict.textContent = branch.difference.matches
                    ? "Matches the recorded decision."
                    : "Differs from the recorded decision.";
                container.append(
                    verdict,
                    createOutcomeRow(branch.outcome, branch.difference.outcomeChanged),
                    createReasonList(branch.reasonCodes, branch.difference.addedReasonCodes, branch.difference.removedReasonCodes),
                    ...renderEndpointResult(branch));

                const stagesDetails = document.createElement("details");
                const summary = document.createElement("summary");
                summary.textContent = "Evaluation stages";
                const stages = document.createElement("ol");
                stages.className = "simulation-stages";
                stages.append(
                    renderConstraints(branch),
                    renderDecisionPolicy(branch),
                    renderCapability(branch),
                    renderReceipt(branch));
                stagesDetails.append(summary, stages);
                container.append(stagesDetails);
                return container;
            };

            const renderEvaluation = (evaluation) => {
                const column = document.createElement("section");
                column.className = evaluation.hypothetical ? "replay-column replay-hypothetical" : "replay-column";
                const heading = document.createElement("h3");
                heading.textContent = evaluation.label;
                const kind = document.createElement("p");
                kind.className = "replay-kind";
                kind.textContent = evaluation.hypothetical ? "What-if" : "Re-verification";
                column.append(heading, kind);
                if (evaluation.hypotheticalReason) {
                    column.append(createNote("output-note", evaluation.hypotheticalReason));
                }

                const policy = createDetails([
                    ["Policy", `${evaluation.policyVersion} ${shortHash(evaluation.policyHash)}`],
                    ["Left out", evaluation.disabledConstraints]
                ]);
                policy.title = evaluation.policyHash;
                column.append(policy);
                if (evaluation.policyChanges.length > 0) {
                    const changes = document.createElement("ul");
                    changes.className = "candidate-policy-changes";
                    evaluation.policyChanges.forEach((change) => {
                        const item = document.createElement("li");
                        item.textContent = change;
                        changes.append(item);
                    });
                    column.append(changes);
                }

                const names = (keys) => keys.map((key) => (factNames[key] || key).toLowerCase()).join(" and ");
                if (evaluation.partial) {
                    column.append(createNote(
                        "replay-partial",
                        `Partial replay: the result depends on the ${names(evaluation.decisiveUnknownInputs)}, ` +
                        "which the ledger does not record. Each possibility is shown."));
                } else if (evaluation.ignoredUnknownInputs.length > 0) {
                    column.append(createNote(
                        "output-note",
                        `The ${names(evaluation.ignoredUnknownInputs)} is not recorded, and made no difference to this result.`));
                }

                evaluation.branches.forEach((branch) => column.append(renderBranch(branch)));
                return column;
            };

            const renderResult = (result) => {
                const original = document.createElement("section");
                original.className = "replay-card replay-original";
                const heading = document.createElement("h3");
                heading.textContent = "Recorded decision";
                const kind = document.createElement("p");
                kind.className = "replay-kind";
                kind.textContent = "As persisted";
                const recordedPolicy = result.decision.policy;
                original.append(
                    heading,
                    kind,
                    createOutcomeRow(result.decision.outcome, false),
                    createReasonList(result.decision.reasonCodes, [], []),
                    createDetails([[
                        "Policy",
                        `${recordedPolicy.recordedVersion || "unversioned"} ${shortHash(recordedPolicy.recordedHash)}`
                    ]]));

                const columns = document.createElement("div");
                columns.className = "replay-columns";
                columns.append(renderEvaluation(result.current));
                if (result.candidate) {
                    columns.append(renderEvaluation(result.candidate));
                }

                const title = document.createElement("h3");
                title.textContent = "Side by side";
                replayResult.replaceChildren(title, original, columns);
            };

            const selectDecision = async (receiptId) => {
                selectedReceiptId = null;
                replayButton.disabled = true;
                replayResult.replaceChildren();
                showOutput("replay");
                replayStatus.textContent = "Finding the decision…";
                try {
                    const { response, payload } = await getJson(`${decisionsPath}/${encodeURIComponent(receiptId)}`);
                    if (response.status === 404) {
                        replayDetails.replaceChildren();
                        if (replayEmpty) {
                            replayEmpty.hidden = false;
                        }

                        replayStatus.textContent = "No decision has that receipt identifier.";
                        markSelected();
                        return;
                    }

                    if (!response.ok || !payload) {
                        replayStatus.textContent = payload?.message || `The lookup returned HTTP ${response.status}.`;
                        return;
                    }

                    selectedReceiptId = payload.decisionReceiptId;
                    receiptInput.value = selectedReceiptId;
                    renderDetails(payload);
                    markSelected();
                    replayButton.disabled = !payload.replayable;
                    replayStatus.textContent = !payload.replayable
                        ? payload.notReplayableReason
                        : payload.policy.matches
                        ? "Decision found. Replay it to check whether it still holds."
                        : "Decision found. It was recorded under a different policy, so a replay is a what-if.";
                } catch {
                    replayStatus.textContent = "The decision could not be looked up.";
                }
            };

            lookupForm.addEventListener("submit", (event) => {
                event.preventDefault();
                const receiptId = receiptInput.value.trim();
                if (!receiptId) {
                    replayStatus.textContent = "Enter a decision receipt identifier.";
                    receiptInput.focus();
                    return;
                }

                void selectDecision(receiptId);
            });

            refreshButton.addEventListener("click", () => void loadRecent(false));
            olderButton.addEventListener("click", () => void loadRecent(true));

            // A result describes the options it was replayed with, so changing them clears it rather than leaving a
            // stale comparison on screen.
            const clearStaleResult = () => {
                if (replayResult.children.length > 0) {
                    replayResult.replaceChildren();
                    replayStatus.textContent = "Options changed. Replay again to compare.";
                }
            };
            replayForm.addEventListener("change", clearStaleResult);

            replayForm.addEventListener("submit", async (event) => {
                event.preventDefault();
                if (!selectedReceiptId) {
                    return;
                }

                const body = new FormData(replayForm);
                replayForm.querySelectorAll("input[data-constraint-name]").forEach((input) => {
                    if (!input.checked) {
                        body.append("disabledConstraints", input.dataset.constraintName);
                    }
                });

                showOutput("replay");
                replayButton.disabled = true;
                replayStatus.textContent = "Replaying the decision…";
                try {
                    const response = await fetch(`${decisionsPath}/${encodeURIComponent(selectedReceiptId)}/replay`, {
                        method: "POST",
                        body,
                        headers: { "Accept": "application/json" },
                        credentials: "same-origin"
                    });
                    const contentType = response.headers.get("content-type") || "";
                    const payload = contentType.includes("json") ? await response.json() : null;
                    if (!response.ok || !payload) {
                        replayResult.replaceChildren();
                        replayStatus.textContent = payload?.message || `The replay returned HTTP ${response.status}.`;
                        return;
                    }

                    renderDetails(payload.decision);
                    renderResult(payload);
                    replayStatus.textContent = "Replayed; nothing was persisted and the stored evidence is unchanged.";
                } catch {
                    replayStatus.textContent = "The replay could not be requested.";
                } finally {
                    replayButton.disabled = !selectedReceiptId;
                }
            });

            replayTab?.addEventListener("click", () => {
                if (!recentLoaded) {
                    void loadRecent(false);
                }
            });
            if (!replayPanel.hidden) {
                void loadRecent(false);
            }
        }
    }

    if (hasPipelineInspector) {
        const pipelinePanel = document.getElementById("explorer-panel-pipeline");
        const pipelineTab = document.getElementById("explorer-tab-pipeline");
        const pipelineForm = document.getElementById("pipeline-inspector-form");
        const correlationInput = document.getElementById("pipeline-correlation");
        const followInput = document.getElementById("pipeline-follow");
        const statusSelect = document.getElementById("pipeline-outbox-status");
        const liveInput = document.getElementById("pipeline-live");
        const pipelineStatus = document.getElementById("pipeline-inspector-status");
        const stagesOutput = document.getElementById("pipeline-stages");
        const outboxOutput = document.getElementById("pipeline-outbox");
        const reconciliationOutput = document.getElementById("pipeline-reconciliation");
        const telemetryOutput = document.getElementById("pipeline-telemetry");

        if (pipelinePanel && pipelineForm && correlationInput && followInput && statusSelect && liveInput &&
            pipelineStatus && stagesOutput && outboxOutput && reconciliationOutput && telemetryOutput) {
            const snapshotPath = "/api/v1/pipeline-inspector/snapshot";
            const pollIntervalMilliseconds = 2000;
            let refreshing = false;
            let lastSnapshotText = null;

            const formatClock = (value) => {
                if (!value) {
                    return null;
                }

                const date = new Date(value);
                const milliseconds = String(date.getMilliseconds()).padStart(3, "0");
                return `${date.toLocaleTimeString([], { hour12: false })}.${milliseconds}`;
            };

            const formatSeconds = (seconds) => (seconds === null || seconds === undefined
                ? null
                : `${Number(seconds).toFixed(seconds < 10 ? 1 : 0)} s`);

            const element = (tag, className, text) => {
                const created = document.createElement(tag);
                if (className) {
                    created.className = className;
                }

                if (text !== undefined && text !== null) {
                    created.textContent = text;
                }

                return created;
            };

            const badge = (status) => element("span", `pipeline-badge pipeline-badge-${String(status).toLowerCase()}`, status);

            const renderStages = (request) => {
                if (!request) {
                    stagesOutput.replaceChildren(element(
                        "p",
                        "output-note",
                        "Run a governed action, or enter a correlation identifier, to time its stages."));
                    return;
                }

                if (!request.found) {
                    stagesOutput.replaceChildren(element(
                        "p",
                        "output-note",
                        `No governance decision is recorded for correlation ${request.correlationId}.`));
                    return;
                }

                const intro = element(
                    "p",
                    "output-note",
                    `Correlation ${request.correlationId}${request.traceId ? ` · trace ${request.traceId}` : ""}. ` +
                    "Offsets are from the first recorded stage, read from timestamps the pipeline already stores.");
                const list = element("ol", "pipeline-stages");
                let boundaryShown = false;
                request.stages.forEach((stage) => {
                    if (!stage.synchronous && !boundaryShown) {
                        boundaryShown = true;
                        const boundary = element("li", "pipeline-boundary");
                        boundary.setAttribute("role", "separator");
                        boundary.append(
                            element("strong", null, "Synchronous | asynchronous"),
                            element("span", null, "The response has been sent; the outbox worker continues in the background."));
                        list.append(boundary);
                    }

                    const item = element("li", `pipeline-stage pipeline-stage-${stage.status}`);
                    const heading = element("div", "pipeline-stage-heading");
                    heading.append(element("span", "pipeline-stage-name", stage.name), badge(stage.status));
                    const timing = element("p", "pipeline-stage-time");
                    timing.textContent = stage.occurredUtc
                        ? `${formatClock(stage.occurredUtc)}${stage.offsetMilliseconds === null
                            ? ""
                            : `  +${stage.offsetMilliseconds.toFixed(1)} ms`}`
                        : "No timestamp";
                    item.append(heading, timing, element("p", "pipeline-stage-source", stage.source));
                    if (stage.note) {
                        item.append(element("p", "pipeline-stage-note", stage.note));
                    }

                    list.append(item);
                });
                stagesOutput.replaceChildren(intro, list);
            };

            const renderOutbox = (outbox, selectedCorrelationId) => {
                if (!outbox.enabled) {
                    outboxOutput.replaceChildren(element("p", "output-note", "The audit-completion outbox is disabled."));
                    return;
                }

                const health = createDetails([
                    ["Waiting", `${outbox.backlogCount} (pending, retrying, or deferred)`],
                    ["Oldest waiting", formatSeconds(outbox.oldestPendingAgeSeconds)],
                    ["Retries", outbox.totalRetryCount],
                    ["Dead-lettered", outbox.deadLetterCount],
                    ["Worker polls every", formatSeconds(outbox.pollIntervalSeconds)],
                    ["Attempts before dead-letter", outbox.maxRetryAttempts]
                ]);

                const list = element("ul", "pipeline-entries");
                outbox.entries.forEach((entry) => {
                    const selected = selectedCorrelationId && entry.correlationId === selectedCorrelationId;
                    const item = element("li", selected ? "pipeline-entry pipeline-entry-selected" : "pipeline-entry");
                    const heading = element("div", "pipeline-stage-heading");
                    heading.append(element("span", "pipeline-stage-name", `Batch ${entry.mutationBatchId}`), badge(entry.status));
                    item.append(heading, createDetails([
                        ["Correlation", entry.correlationId],
                        ["Staged", formatClock(entry.createdUtc)],
                        ["Attempts failed", entry.retryCount > 0 ? `${entry.retryCount} of ${outbox.maxRetryAttempts}` : null],
                        ["Last attempt", formatClock(entry.lastAttemptUtc)],
                        ["Next attempt", formatClock(entry.nextAttemptUtc)],
                        ["Delivered", formatClock(entry.deliveredUtc)],
                        ["Last error", entry.lastErrorCode
                            ? `${entry.lastErrorCode}${entry.lastErrorMessage ? `: ${entry.lastErrorMessage}` : ""}`
                            : null],
                        ["Lifecycle event", `${entry.lifecycleEventAppended ? "appended" : "not yet appended"} · ${entry.lifecycleEventId}`]
                    ]));
                    list.append(item);
                });

                if (outbox.entries.length === 0) {
                    list.append(element(
                        "li",
                        "output-note",
                        outbox.statusFilter ? `No entries are ${outbox.statusFilter}.` : "No entries have been staged."));
                }

                outboxOutput.replaceChildren(health, list);
            };

            const renderReconciliation = (reconciliation) => {
                if (!reconciliation.enabled) {
                    reconciliationOutput.replaceChildren(element("p", "output-note", "Audit reconciliation is disabled."));
                    return;
                }

                const summary = createDetails([
                    ["Last run", reconciliation.lastRunUtc
                        ? new Date(reconciliation.lastRunUtc).toLocaleString()
                        : "Not yet run in this process"],
                    ["Worker", reconciliation.workerEnabled
                        ? `Runs every ${formatSeconds(reconciliation.intervalSeconds)}`
                        : "Not running"],
                    ["Open findings", reconciliation.openFindingCount],
                    ["Errors", reconciliation.errorFindingCount],
                    ["Critical", reconciliation.criticalFindingCount],
                    ["Missing completions", reconciliation.missingCompletionCount],
                    ["Stale deliveries", reconciliation.staleDeliveryCount],
                    ["Dead letters", reconciliation.deadLetterCount],
                    ["Manifest failures", reconciliation.manifestVerificationFailureCount]
                ]);

                const findings = element("ul", "pipeline-entries");
                reconciliation.openFindings.forEach((finding) => {
                    const item = element("li", "pipeline-entry");
                    const heading = element("div", "pipeline-stage-heading");
                    heading.append(element("span", "pipeline-stage-name", finding.reasonCode), badge(finding.severity));
                    item.append(heading, createDetails([
                        ["Batch", finding.mutationBatchId],
                        ["Destination", finding.destination],
                        ["First seen", new Date(finding.firstObservedUtc).toLocaleString()],
                        ["Last seen", new Date(finding.lastObservedUtc).toLocaleString()]
                    ]), element("p", "pipeline-stage-note", finding.guidance));
                    findings.append(item);
                });

                if (reconciliation.openFindings.length === 0) {
                    findings.append(element("li", "output-note", "No open findings."));
                }

                reconciliationOutput.replaceChildren(summary, findings);
            };

            const renderTelemetry = (telemetry, request) => {
                const children = [createDetails([
                    ["Tracing", telemetry.tracingEnabled ? "enabled" : "disabled"],
                    ["Metrics", telemetry.metricsEnabled ? "enabled" : "disabled"],
                    ["OTLP export", telemetry.otlpExportEnabled ? "enabled" : "disabled"]
                ])];

                if (request && request.found) {
                    children.push(element("h4", null, "Spans for the selected request"));
                    const spans = element("ul", "pipeline-entries");
                    request.spans.forEach((span) => {
                        const item = element("li", "pipeline-entry");
                        const heading = element("div", "pipeline-stage-heading");
                        heading.append(element("span", "pipeline-stage-name", span.name), badge(span.kind));
                        item.append(heading, createDetails([
                            ["Source", span.source],
                            ["Started", formatClock(span.startUtc)],
                            ["Duration", `${span.durationMilliseconds.toFixed(1)} ms`],
                            ["Status", span.status],
                            ...Object.entries(span.tags)
                        ]));
                        spans.append(item);
                    });
                    if (request.spans.length === 0) {
                        spans.append(element("li", "output-note", "No spans were observed for this request's trace."));
                    }

                    children.push(spans, element("h4", null, "Metrics recorded for the selected request"));
                    const measurements = element("ul", "pipeline-entries");
                    request.measurements.forEach((measurement) => {
                        const item = element("li", "pipeline-entry");
                        item.append(
                            element("p", "pipeline-stage-name", `${measurement.instrument}: ${measurement.value.toFixed(4)} ${measurement.unit || ""}`),
                            createDetails([["Meter", measurement.meter], ...Object.entries(measurement.tags)]));
                        measurements.append(item);
                    });
                    if (request.measurements.length === 0) {
                        measurements.append(element("li", "output-note", "No measurement was matched to this request."));
                    }

                    children.push(
                        measurements,
                        element("p", "output-note", "Measurements carry no trace identifier; they are matched by route, method, and time."));
                }

                if (telemetry.hostGauges.length > 0) {
                    children.push(element("h4", null, "Host gauges"), createDetails(
                        telemetry.hostGauges.map((gauge) => [gauge.instrument, gauge.value])));
                }

                const sources = element("details", "pipeline-telemetry-list");
                sources.append(element("summary", null, `Activity sources in the process (${telemetry.activitySources.length})`));
                const sourceList = element("ul");
                telemetry.activitySources.forEach((source) => {
                    sourceList.append(element(
                        "li",
                        null,
                        `${source.name}${source.version ? ` ${source.version}` : ""}: ` +
                        `${source.addedToOpenTelemetry ? "collected by OpenTelemetry" : "not collected"}, ` +
                        `${source.spansObserved} spans observed`));
                });
                sources.append(sourceList);

                const meters = element("details", "pipeline-telemetry-list");
                meters.append(element("summary", null, `Meters in the process (${telemetry.meters.length})`));
                const meterList = element("ul");
                telemetry.meters.forEach((meter) => {
                    const item = element(
                        "li",
                        null,
                        `${meter.name}: ${meter.addedToOpenTelemetry ? "collected by OpenTelemetry" : "not collected"}`);
                    const instruments = element("ul");
                    meter.instruments.forEach((instrument) => {
                        instruments.append(element(
                            "li",
                            null,
                            `${instrument.name} (${instrument.kind}${instrument.unit ? `, ${instrument.unit}` : ""})`));
                    });
                    item.append(instruments);
                    meterList.append(item);
                });
                meters.append(meterList);

                children.push(sources, meters);
                telemetryOutput.replaceChildren(...children);
            };

            const render = (snapshot) => {
                renderStages(snapshot.request);
                renderOutbox(snapshot.outbox, snapshot.request?.correlationId);
                renderReconciliation(snapshot.reconciliation);
                renderTelemetry(snapshot.telemetry, snapshot.request);
            };

            const refresh = async () => {
                if (refreshing) {
                    return;
                }

                if (followInput.checked && activeCorrelationId && correlationInput.value !== activeCorrelationId) {
                    correlationInput.value = activeCorrelationId;
                }

                const query = new URLSearchParams();
                if (correlationInput.value.trim()) {
                    query.set("correlationId", correlationInput.value.trim());
                }

                if (statusSelect.value) {
                    query.set("status", statusSelect.value);
                }

                refreshing = true;
                try {
                    const queryText = query.toString();
                    const url = queryText ? `${snapshotPath}?${queryText}` : snapshotPath;
                    const response = await fetch(url, { headers: { "Accept": "application/json" }, credentials: "same-origin" });
                    const text = await response.text();
                    if (!response.ok) {
                        const payload = text && (response.headers.get("content-type") || "").includes("json") ? JSON.parse(text) : null;
                        pipelineStatus.textContent = payload?.message || `The pipeline read returned HTTP ${response.status}.`;
                        return;
                    }

                    const snapshot = JSON.parse(text);
                    // Re-render only when something other than the read time changed, so polling does not disturb
                    // reading or focus.
                    const comparable = JSON.stringify({ ...snapshot, generatedUtc: null });
                    if (comparable !== lastSnapshotText) {
                        lastSnapshotText = comparable;
                        render(snapshot);
                    }

                    pipelineStatus.textContent = `Read at ${formatClock(snapshot.generatedUtc)}.` +
                        (liveInput.checked ? " Refreshing every 2 seconds while this tab is open." : "");
                } catch {
                    pipelineStatus.textContent = "The pipeline could not be read.";
                } finally {
                    refreshing = false;
                }
            };

            const isWatching = () => !pipelinePanel.hidden && document.visibilityState === "visible";

            pipelineForm.addEventListener("submit", (event) => {
                event.preventDefault();
                void refresh();
            });
            correlationInput.addEventListener("input", () => {
                followInput.checked = false;
            });
            statusSelect.addEventListener("change", () => void refresh());
            followInput.addEventListener("change", () => {
                if (followInput.checked) {
                    void refresh();
                }
            });
            pipelineTab?.addEventListener("click", () => void refresh());

            // Bounded polling: one read every two seconds at most, and only while the tab is open and the page is
            // visible. Retries and dead-letters send no stream hint, so polling is what shows them.
            window.setInterval(() => {
                if (liveInput.checked && isWatching()) {
                    void refresh();
                }
            }, pollIntervalMilliseconds);

            evidenceHintListeners.push((notification) => {
                if (!isWatching()) {
                    return;
                }

                void refresh();
                if (notification.kind === "lifecycle-event") {
                    // Delivery publishes the hint before the outbox entry is marked delivered; read again after it commits.
                    window.setTimeout(() => void refresh(), 750);
                }
            });

            if (!pipelinePanel.hidden) {
                void refresh();
            }
        }
    }

    if (hasFailureScenarios) {
        const scenariosPanel = document.getElementById("explorer-panel-scenarios");
        const scenarioStatus = document.getElementById("failure-scenario-status");
        const scenarioResult = document.getElementById("failure-scenario-result");
        const scenarioEmpty = document.getElementById("failure-scenario-empty");
        const runButtons = Array.from(document.querySelectorAll("[data-run-scenario]"));

        if (scenariosPanel && scenarioStatus && scenarioResult && scenarioEmpty) {
            const scenariosPath = "/api/v1/failure-scenarios";
            const evidenceTamperingId = "evidence-tampering";
            const pollIntervalMilliseconds = 2000;
            const settledOutboxStatuses = ["Delivered", "Failed", "DeadLettered"];
            const actionBodyText = {
                "not-run": "The governed action body did not run.",
                "ran": "The governed action body ran.",
                "ran-rolled-back": "The governed action body ran; its change was rolled back."
            };
            let activeRun = null;
            let loadingRun = false;

            const element = (tag, className, text) => {
                const created = document.createElement(tag);
                if (className) {
                    created.className = className;
                }

                if (text !== undefined && text !== null) {
                    created.textContent = text;
                }

                return created;
            };

            const badge = (status, tone) => element("span", `pipeline-badge pipeline-badge-${tone || String(status).toLowerCase()}`, status);

            const formatTime = (value) => (value ? new Date(value).toLocaleString() : null);

            const card = (title, ...children) => {
                const section = element("section", "replay-card");
                section.append(element("h3", null, title), ...children);
                return section;
            };

            const details = (entries) => {
                const list = document.createElement("dl");
                entries.forEach(([label, value]) => appendDetail(list, label, value));
                return list;
            };

            const setRunning = (running) => {
                runButtons.forEach((button) => {
                    button.disabled = running;
                });
            };

            const markCurrent = (scenarioId) => {
                document.querySelectorAll(".failure-scenario").forEach((item) => {
                    if (item.dataset.scenarioId === scenarioId) {
                        item.setAttribute("aria-current", "true");
                    } else {
                        item.removeAttribute("aria-current");
                    }
                });
            };

            const postForm = (path, fields, headers) => {
                const formData = new FormData();
                formData.append("__RequestVerificationToken", requestToken?.value || "");
                Object.entries(fields || {}).forEach(([name, value]) => formData.append(name, value));
                return fetch(path, {
                    method: "POST",
                    body: formData,
                    headers: { "Accept": "application/json", ...(headers || {}) },
                    credentials: "same-origin"
                });
            };

            const readMessage = async (response) => {
                try {
                    const payload = await response.json();
                    return payload.message || payload.title || payload.detail || `HTTP ${response.status}`;
                } catch {
                    return `HTTP ${response.status}`;
                }
            };

            const renderTamperingVerification = (label, verification) => {
                const verified = verification.status === "verified";
                const row = element("div");
                row.append(
                    element("strong", null, `${label}: `),
                    badge(verification.status, verified ? "delivered" : "failed"));
                const hashes = details([
                    ["Stored hash", verification.storedHash],
                    ["Computed hash", verification.computedHash]
                ]);
                return [row, hashes];
            };

            const renderTampering = (tampering) => {
                scenarioEmpty.hidden = true;
                scenarioResult.replaceChildren(
                    card(
                        "6. Evidence tampering",
                        element("p", null, "An in-memory copy of the stored decision record was changed and verified. The stored row was not modified."),
                        details([
                            ["Correlation", tampering.correlationId],
                            ["Record", tampering.recordId],
                            ["Changed field", tampering.field],
                            ["Stored value", tampering.storedValue],
                            ["Copy's value", tampering.tamperedValue]
                        ])),
                    card(
                        "Verification",
                        ...renderTamperingVerification("Unchanged copy", tampering.unchangedCopy),
                        ...renderTamperingVerification("Changed copy", tampering.tamperedCopy),
                        ...renderTamperingVerification("Stored record, read again", tampering.storedRecord)));
            };

            const renderTimelineEntries = (timeline) => {
                const entries = timeline?.entries || [];
                if (entries.length === 0) {
                    return element("p", "output-note", "The request left no persisted evidence.");
                }

                const list = element("ol", "failure-scenario-timeline");
                entries.forEach((entry) => {
                    const parts = [entry.kind.replaceAll("-", " ")];
                    if (entry.outcome) {
                        parts.push(entry.outcome);
                    }

                    if (entry.status) {
                        parts.push(entry.status);
                    }

                    if (entry.reasonCodes?.length) {
                        parts.push(entry.reasonCodes.join(", "));
                    }

                    list.append(element("li", null, `${formatTime(entry.occurredUtc)}: ${parts.join(" · ")}`));
                });
                return list;
            };

            const renderReport = (report) => {
                const run = report.run;
                const scenario = report.scenario;
                scenarioEmpty.hidden = true;
                const children = [];

                const outcome = details([
                    ["Run", run.status],
                    ["HTTP status", run.statusCode],
                    ["Exception", run.exceptionType],
                    ["Exception reason", run.exceptionReasonCode],
                    ["Faults injected", run.faults.map((fault) => fault.point)],
                    ["Correlation", run.correlationId]
                ]);
                const outcomeCard = card(`${scenario.number}. ${scenario.title}`, element("p", null, scenario.expectation), outcome);
                if (report.actionBody) {
                    outcomeCard.append(element("p", null, actionBodyText[report.actionBody] || report.actionBody));
                }

                children.push(outcomeCard);

                const decisions = card("Decision ledger");
                if (report.decisions.length === 0) {
                    decisions.append(element("p", "output-note", "No decision was recorded for this request."));
                } else {
                    report.decisions.forEach((decision) => {
                        decisions.append(details([
                            ["Outcome", decision.outcome],
                            ["Reasons", decision.reasonCodes],
                            ["Capability token", decision.capabilityTokenId],
                            ["Record", decision.recordId]
                        ]));
                    });
                }

                decisions.append(element("p", null, `Mutation-audit records committed: ${report.mutationAuditRecordCount}.`));
                children.push(decisions);

                if (report.outbox) {
                    const outbox = report.outbox;
                    const outboxCard = card("Audit-completion outbox");
                    const statusRow = element("div");
                    statusRow.append(element("strong", null, "Status: "), badge(outbox.status));
                    outboxCard.append(statusRow, details([
                        ["Failed attempts", `${outbox.retryCount} of ${outbox.maxRetryAttempts} before dead-lettering`],
                        ["Last attempt", formatTime(outbox.lastAttemptUtc)],
                        ["Next attempt", formatTime(outbox.nextAttemptUtc)],
                        ["Delivered", formatTime(outbox.deliveredUtc)],
                        ["Last error", outbox.lastErrorCode
                            ? `${outbox.lastErrorCode}${outbox.lastErrorMessage ? ` (${outbox.lastErrorMessage})` : ""}`
                            : null],
                        ["Lifecycle event", outbox.lifecycleEventAppended ? "appended" : "not appended"]
                    ]));
                    if (run.deliveryFaultActive) {
                        const clearButton = element("button", "secondary-button", "Clear the delivery fault");
                        clearButton.type = "button";
                        clearButton.addEventListener("click", async () => {
                            clearButton.disabled = true;
                            const response = await postForm(`${scenariosPath}/runs/${encodeURIComponent(run.runId)}/clear-delivery-fault`);
                            scenarioStatus.textContent = response.ok
                                ? "Delivery fault cleared. The outbox delivers the entry at its next attempt."
                                : await readMessage(response);
                            await loadRun();
                        });
                        outboxCard.append(
                            element("p", null, "Delivery keeps failing until the fault is cleared; the outbox worker retries on its backoff schedule."),
                            clearButton);
                    } else if (run.deliveryFaultClearedUtc) {
                        outboxCard.append(element("p", null, `Delivery fault cleared at ${formatTime(run.deliveryFaultClearedUtc)}.`));
                    }

                    children.push(outboxCard);
                }

                if (report.reconciliation) {
                    const reconciliation = report.reconciliation;
                    children.push(card(
                        "Reconciliation",
                        element("p", null, reconciliation.explanation),
                        details([
                            ["Registered", reconciliation.registered ? "yes" : "no"],
                            ["Last run", formatTime(reconciliation.lastRunUtc)],
                            ["Open findings", reconciliation.registered ? reconciliation.openFindingCount : null],
                            ["Mutation batches", reconciliation.mutationBatchIds.length ? reconciliation.mutationBatchIds : "none"],
                            ["Findings for this request", reconciliation.findingReasonCodes.length ? reconciliation.findingReasonCodes : "none"]
                        ])));
                }

                children.push(card("Evidence timeline", renderTimelineEntries(report.timeline)));
                scenarioResult.replaceChildren(...children);
            };

            const isPolling = () => activeRun?.scenarioId === "outbox-delivery-fails" && !activeRun.settled;

            const loadRun = async () => {
                if (!activeRun || loadingRun) {
                    return;
                }

                loadingRun = true;
                try {
                    const response = await fetch(
                        `${scenariosPath}/runs/${encodeURIComponent(activeRun.runId)}`,
                        { headers: { "Accept": "application/json" }, credentials: "same-origin" });
                    if (!response.ok) {
                        scenarioStatus.textContent = await readMessage(response);
                        return;
                    }

                    const text = await response.text();
                    const report = JSON.parse(text);
                    activeRun.correlationId = report.run.correlationId;
                    activeRun.settled = !report.outbox || settledOutboxStatuses.includes(report.outbox.status);
                    // Re-render only when the report changed, so polling does not disturb reading or focus.
                    if (text !== activeRun.lastReportText) {
                        activeRun.lastReportText = text;
                        renderReport(report);
                    }
                } catch {
                    scenarioStatus.textContent = "The scenario result could not be read.";
                } finally {
                    loadingRun = false;
                }
            };

            const runScenario = async (button) => {
                const scenarioId = button.dataset.runScenario;
                markCurrent(scenarioId);
                setRunning(true);
                activeRun = null;
                scenarioResult.replaceChildren();
                scenarioEmpty.hidden = false;
                scenarioStatus.textContent = "Arming the scenario…";

                try {
                    // The tampering scenario copies the selected request's decision, or the newest one.
                    const armFields = scenarioId === evidenceTamperingId && activeCorrelationId
                        ? { correlationId: activeCorrelationId }
                        : {};
                    const armed = await postForm(`${scenariosPath}/${encodeURIComponent(scenarioId)}`, armFields);
                    if (!armed.ok) {
                        scenarioStatus.textContent = await readMessage(armed);
                        return;
                    }

                    const payload = await armed.json();
                    if (payload.tampering) {
                        renderTampering(payload.tampering);
                        scenarioStatus.textContent = "Verified an unchanged and a changed copy of the stored decision record.";
                        return;
                    }

                    const run = payload.run;
                    activeRun = { runId: run.runId, scenarioId, settled: false, correlationId: null, lastReportText: null };
                    scenarioStatus.textContent = `Armed for one request. Sending ${run.request.method} ${run.request.path}…`;
                    const response = await postForm(run.request.path, run.request.fields, run.request.headers);
                    scenarioStatus.textContent = `The governed endpoint answered HTTP ${response.status}.`;
                    await loadRun();
                } catch {
                    scenarioStatus.textContent = "The scenario could not be run.";
                } finally {
                    setRunning(false);
                }
            };

            runButtons.forEach((button) => {
                button.addEventListener("click", () => void runScenario(button));
            });

            // Retries send no evidence hint, so a delivery-failure run is re-read every two seconds while this tab is
            // open, until the entry is delivered, fails, or is dead-lettered.
            window.setInterval(() => {
                if (isPolling() && !scenariosPanel.hidden && document.visibilityState === "visible") {
                    void loadRun();
                }
            }, pollIntervalMilliseconds);

            evidenceHintListeners.push((notification) => {
                if (activeRun?.correlationId && notification.correlationId === activeRun.correlationId) {
                    void loadRun();
                }
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
                notifyEvidenceHint(notification);
                return;
            }

            notifyEvidenceHint(notification);

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
