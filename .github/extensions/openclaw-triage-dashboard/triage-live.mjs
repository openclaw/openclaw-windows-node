export function selectExactLookupItems(items, pullRequests, issues) {
    const pullRequestsByNumber = new Map(pullRequests.map((item) => [item.number, item]));
    const issueNumbers = new Set(issues.map((item) => item.number));

    return items.filter((item) => {
        if (item.type === "issue") {
            return !issueNumbers.has(item.number);
        }

        const live = pullRequestsByNumber.get(item.number);
        if (!live) {
            return true;
        }

        return live.isDraft === true ||
            (live.mergeable === "MERGEABLE" && live.mergeStateStatus === "BLOCKED");
    });
}

export function exactLookupResultIsValid(item, value) {
    if (!value || value.number !== item.number) return false;
    const state = String(value.state ?? "").toUpperCase();
    if (item.type !== "pr") {
        return state === "OPEN" || state === "CLOSED";
    }
    return (state === "OPEN" || state === "CLOSED" || state === "MERGED") &&
        typeof value.isDraft === "boolean" &&
        typeof value.mergeStateStatus === "string" &&
        value.mergeStateStatus.length > 0 &&
        typeof value.headRefOid === "string" &&
        value.headRefOid.length > 0 &&
        Array.isArray(value.statusCheckRollup);
}
