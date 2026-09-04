function itemKind(type) {
    return type === "pr" ? "PR" : "Issue";
}

export function buildSubsessionRoutingPrompt(repo, item, actionPrompt) {
    const kind = itemKind(item.type);
    const sessionName = `Triage ${kind} #${item.number}`;
    const identity = `${repo} ${kind} #${item.number}`;

    return {
        sessionName,
        prompt:
            `Route this dashboard action to the dedicated child project session for ${identity}. ` +
            "Do not execute the item work in this parent session.\n\n" +
            "Use the app session tools as follows:\n" +
            `1. Call list_projects and resolve the project whose GitHub repository is exactly "${repo}". ` +
            "Use that project's ID for any create_session call.\n" +
            `2. Call list_sessions_and_chats and look for an existing child project session named ` +
            `"${sessionName}" in the current project and repository.\n` +
            "3. If it exists, call send_session_message with the action below so the work is appended " +
            "to that session.\n" +
            `4. If it does not exist, call create_session with the resolved project_id, name "${sessionName}", ` +
            "coordinate_with_creator enabled, base_branch unset, and a kickoff using the action below in autopilot mode.\n" +
            "5. Do not create a duplicate session. Briefly report whether the child session was created or reused.\n\n" +
            `Action for the child session:\n${actionPrompt}`,
    };
}
