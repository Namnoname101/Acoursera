namespace AutomationPlatform.Presentation.Services;

/// <summary>Read-only identity extraction. Never derives a user ID from a name or email.</summary>
internal static class CourseraProfileIdentityProbe
{
    // Course startup needs an authenticated account ID for isolated local state,
    // not a profile-name check or an account-settings navigation. Name is optional.
    public const string CourseAccountScript = """
        (function() {
            const user = window.coursera && window.coursera.user ? window.coursera.user : {};
            return JSON.stringify({
                userId: String(user.id || '').trim(),
                fullName: String(user.full_name || user.fullName || '').trim()
            });
        })();
        """;

    public const string Script = """
        (function() {
            const normalize = value => String(value || '')
                .normalize('NFC').replace(/\u00a0/g, ' ').replace(/\s+/g, ' ').trim();
            const user = window.coursera && window.coursera.user ? window.coursera.user : {};
            const userId = normalize(user.id);
            let fullName = normalize(user.full_name || user.fullName);
            if (!fullName) {
                const header = document.querySelector('button[data-e2e="header-profile"]');
                const ariaLabel = normalize(header?.getAttribute('aria-label'));
                const prefix = 'User dropdown menu for ';
                if (ariaLabel.toLocaleLowerCase().startsWith(prefix.toLocaleLowerCase())) {
                    fullName = normalize(ariaLabel.slice(prefix.length));
                }
            }
            if (!fullName && /\/account-settings\/?$/i.test(location.pathname)) {
                const fieldText = el => {
                    const labels = Array.from(el.labels || []).map(label => label.innerText);
                    const referenced = ['aria-labelledby', 'aria-describedby'].flatMap(attr =>
                        (el.getAttribute(attr) || '').split(/\s+/).filter(Boolean)
                            .map(id => document.getElementById(id)?.innerText || ''));
                    return normalize([el.name, el.id, el.placeholder, el.getAttribute('aria-label'),
                        ...labels, ...referenced].filter(Boolean).join(' '));
                };
                const visible = el => {
                    if (el.closest('[aria-hidden="true"],[hidden],[inert]')) return false;
                    for (let current = el; current; current = current.parentElement) {
                        const style = getComputedStyle(current);
                        if (style.display === 'none' || style.visibility === 'hidden' ||
                            style.visibility === 'collapse' || Number(style.opacity) === 0) return false;
                    }
                    return Array.from(el.getClientRects()).some(r => r.width > 0 && r.height > 0);
                };
                const fields = Array.from(document.querySelectorAll('input:not([type]),input[type="text"]'))
                    .filter(visible).filter(el => /(^|\s)full\s*name(\s|$)/i.test(fieldText(el)));
                if (fields.length === 1) fullName = normalize(fields[0].value);
            }
            return JSON.stringify({ userId, fullName });
        })();
        """;
}
