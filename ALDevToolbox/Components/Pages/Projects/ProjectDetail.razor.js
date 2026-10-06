// The solution page's tab row (#1077). A tab click switches the section in page
// state and only rewrites the address: General and Repositories share one Save,
// and a navigation builds the page afresh and drops what was typed. Replaced
// rather than pushed, so Back leaves the solution instead of stepping through
// tabs that would each be that fresh build. On a phone the row scrolls
// sideways, so the chosen tab is brought into view as well.
export function showTab(url) {
    if (url) history.replaceState(history.state, "", url);
    const active = document.querySelector(".page.sol .settings__tabs .header-tab.is-active");
    if (active) active.scrollIntoView({ block: "nearest", inline: "nearest" });
}
