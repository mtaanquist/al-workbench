// Closes the notification bell's flyout when a link in it is followed.
//
// The flyout is a native popover (NotificationBell.razor), so the browser
// already opens it, closes it on Escape or an outside click, and hands focus
// back to the bell. What it cannot know is that following a link moved on:
// Blazor's enhanced navigation patches the page in place and keeps the
// popover element, open, over the page the link went to. Delegated from the
// document, so it needs no rescan after a navigation.
(function () {
    function hide(flyout) {
        if (flyout && flyout.matches(":popover-open")) flyout.hidePopover();
    }

    document.addEventListener("click", function (e) {
        var link = e.target.closest && e.target.closest(".notif-flyout a[href]");
        if (link) hide(link.closest(".notif-flyout"));
    });

    // Belt and braces for a navigation that did not start with a click here
    // (the back button, the command palette).
    document.addEventListener("enhancedload", function () {
        hide(document.getElementById("notif-flyout"));
    });
})();
