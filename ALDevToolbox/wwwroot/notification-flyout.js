// The notification bell's flyout (NotificationBell.razor).
//
// The flyout is a native popover, so the browser already opens it, closes it
// on Escape or an outside click, and hands focus back to the bell. This adds
// the two things it cannot know:
//   - following a link moved on: Blazor's enhanced navigation patches the page
//     in place and keeps the popover element, open, over the page the link
//     went to, so a click on a link closes it;
//   - "Mark all as read" should not reload the page underneath, which would
//     drop anything typed into a form there. The post goes in the background
//     and the flyout and the bell are marked read in place. If it fails, the
//     form is submitted normally, which still works (it comes back to this
//     page).
// Delegated from the document, so it needs no rescan after a navigation.
(function () {
    function hide(flyout) {
        if (flyout && flyout.matches(":popover-open")) flyout.hidePopover();
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
        const form = flyout.querySelector("form");
        if (form) form.remove();
    }

    document.addEventListener("click", function (e) {
        const link = e.target.closest && e.target.closest(".notif-flyout a[href]");
        if (link) hide(link.closest(".notif-flyout"));
    });

    document.addEventListener("submit", function (e) {
        const form = e.target;
        const flyout = form.closest && form.closest(".notif-flyout");
        if (!flyout || form.dataset.sending) return;
        e.preventDefault();
        form.dataset.sending = "1";
        fetch(form.action, { method: "POST", body: new FormData(form), redirect: "manual", credentials: "same-origin" })
            .then(function (r) {
                // A redirect (opaque here) is the endpoint's success answer.
                if (r.type === "opaqueredirect" || r.ok) markedRead(flyout);
                else form.submit();
            })
            .catch(function () { form.submit(); });
    }, true);

    // Belt and braces for a navigation that did not start with a click here
    // (the back button, the command palette).
    document.addEventListener("enhancedload", function () {
        hide(document.getElementById("notif-flyout"));
    });
})();
