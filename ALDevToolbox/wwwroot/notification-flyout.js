// The notification bell's flyout (NotificationBell.razor).
//
// The flyout is a native popover, so the browser already opens it, closes it
// on Escape or an outside click, and hands focus back to the bell. This adds
// the two things it cannot know:
//   - following a link moved on: Blazor's enhanced navigation patches the page
//     in place and keeps the popover element, open, over the page the link
//     went to, so a plain click on a link closes it;
//   - "Mark all as read" should not reload the page underneath, which would
//     drop anything typed into a form there. The post goes in the background
//     and the flyout and the bell are marked read in place. Only a 204 counts
//     as done: an expired session answers with a redirect to the login page,
//     and a failure says so in the flyout rather than navigating away.
// Without this script the form posts normally and comes back to the same page.
// Delegated from the document, so it needs no rescan after a navigation.
(function () {
    function hide(flyout) {
        if (flyout && flyout.matches(":popover-open")) flyout.hidePopover();
    }

    function say(flyout, text) {
        const status = flyout.querySelector(".notif-flyout__status");
        if (status) status.textContent = text;
    }

    function markedRead(flyout) {
        for (const row of flyout.querySelectorAll(".notif--unread")) {
            row.classList.remove("notif--unread");
            const sr = row.querySelector(".notif__title .u-sr");
            if (sr) sr.remove();
        }
        const bell = document.querySelector(".notif-bell");
        if (bell) {
            const count = bell.querySelector(".notif-bell__count");
            if (count) count.remove();
            bell.setAttribute("aria-label", "Notifications");
            bell.setAttribute("title", "Notifications");
        }
        // The button goes, so focus moves to the heading rather than the page.
        const title = flyout.querySelector(".notif-flyout__title");
        if (title) title.focus();
        const form = flyout.querySelector("form");
        if (form) form.remove();
        say(flyout, "All marked as read");
    }

    document.addEventListener("click", function (e) {
        // A click that opens a new tab leaves this page where it was.
        if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) return;
        const link = e.target.closest && e.target.closest(".notif-flyout a[href]");
        if (link) hide(link.closest(".notif-flyout"));
    });

    document.addEventListener("submit", function (e) {
        const form = e.target;
        const flyout = form.closest && form.closest(".notif-flyout");
        if (!flyout) return;
        e.preventDefault();
        if (form.dataset.sending) return;
        form.dataset.sending = "1";
        say(flyout, "");
        fetch(form.action, {
            method: "POST",
            body: new FormData(form),
            headers: { "X-Notifications-Background": "1" },
            redirect: "manual",
            credentials: "same-origin",
        })
            .then(function (r) {
                if (r.status === 204) markedRead(flyout);
                else say(flyout, "Could not mark them read. Reload the page and try again.");
            })
            .catch(function () { say(flyout, "Could not mark them read. Reload the page and try again."); })
            .finally(function () { delete form.dataset.sending; });
    }, true);

    // Belt and braces for a navigation that did not start with a click here
    // (the back button, the command palette).
    document.addEventListener("enhancedload", function () {
        hide(document.getElementById("notif-flyout"));
    });
})();
