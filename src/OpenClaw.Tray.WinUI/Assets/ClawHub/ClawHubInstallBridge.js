(function () {
    "use strict";

    var buttonAttribute = "data-openclaw-windows-hub-install";
    var bridgeAttribute = "data-openclaw-windows-hub-bridge";
    var kindAttribute = "data-openclaw-windows-hub-kind";
    var idAttribute = "data-openclaw-windows-hub-id";
    var packageAttribute = "data-openclaw-windows-hub-package";

    function getListing() {
        if (window.location.hostname.toLowerCase() !== "clawhub.ai") {
            return null;
        }

        var segments = window.location.pathname.split("/").filter(function (segment) {
            return segment.length > 0;
        });
        var listingIndex = segments.findIndex(function (segment) {
            return segment === "plugins" || segment === "skills";
        });
        if (listingIndex < 1 || listingIndex + 1 >= segments.length) {
            return null;
        }

        var kind = segments[listingIndex] === "skills" ? "skill" : "plugin";
        var listingId = segments[listingIndex + 1];
        if (kind === "skill") {
            var publisher = segments[listingIndex - 1].replace(/^@/, "");
            listingId = "@" + publisher + "/" + listingId;
        }
        return { kind: kind, id: listingId };
    }

    function getInstallTarget(commandWrap) {
        if (!commandWrap) {
            return null;
        }
        var commandTarget = commandWrap.querySelector(".skill-install-command-target");
        if (!commandTarget) {
            return null;
        }
        return commandTarget.textContent.trim() || null;
    }

    function replaceInstallCommands() {
        var listing = getListing();
        if (!listing) {
            return;
        }

        document.querySelectorAll(".skill-install-command-card").forEach(function (installCard) {
            var button = installCard.querySelector("[" + buttonAttribute + "]");
            var commandWrap = installCard.querySelector(".skill-install-command-wrap");
            var installTarget = getInstallTarget(commandWrap);
            var packageMatch = listing.kind === "plugin" && installTarget
                ? installTarget.match(/^clawhub:(.+)$/)
                : null;
            var packageName = packageMatch
                ? packageMatch[1].trim()
                : (button ? button.getAttribute(packageAttribute) : null);
            var listingId = listing.kind === "skill"
                ? installTarget ||
                    (button ? button.getAttribute(idAttribute) : null)
                : listing.id;
            if (!button && !installTarget) {
                return;
            }

            if (!button) {
                button = document.createElement("button");
                button.type = "button";
                button.setAttribute(buttonAttribute, "true");
                button.textContent = "Install via Windows Hub";
                button.style.cssText =
                    "width:100%;min-height:42px;padding:0 18px;border:0;border-radius:10px;" +
                    "background:#e5488f;color:white;font:600 14px system-ui;cursor:pointer;";
                button.addEventListener("click", function () {
                    var kind = button.getAttribute(kindAttribute);
                    var id = button.getAttribute(idAttribute);
                    if (!kind || !id) {
                        return;
                    }
                    var packageValue = button.getAttribute(packageAttribute);
                    window.location.href =
                        "openclaw://clawhub/install?kind=" +
                        encodeURIComponent(kind) +
                        "&id=" + encodeURIComponent(id) +
                        (kind === "plugin" && packageValue
                            ? "&package=" + encodeURIComponent(packageValue)
                            : "");
                });
            }

            button.setAttribute(kindAttribute, listing.kind);
            button.setAttribute(idAttribute, listingId);
            if (packageName) {
                button.setAttribute(packageAttribute, packageName);
            } else {
                button.removeAttribute(packageAttribute);
            }
            button.setAttribute(
                "aria-label",
                "Install this " + listing.kind + " via Windows Hub");
            if (commandWrap) {
                commandWrap.replaceWith(button);
            }
        });
    }

    replaceInstallCommands();
    if (!document.documentElement.hasAttribute(bridgeAttribute)) {
        document.documentElement.setAttribute(bridgeAttribute, "ready");
        new MutationObserver(replaceInstallCommands).observe(document.documentElement, {
            childList: true,
            subtree: true
        });
    }
}());
