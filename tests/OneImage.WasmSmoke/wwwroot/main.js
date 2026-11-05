// Starts the app and reports how it ended through the page title, which run-browser.mjs polls.
import { dotnet } from './_framework/dotnet.js';

try {
    const { runMain } = await dotnet.withExitOnUnhandledError().create();
    const code = await runMain();
    document.title = code === 0 ? 'smoke:pass' : 'smoke:fail:' + code;
} catch (e) {
    console.error('smoke exception: ' + (e && e.stack ? e.stack : e));
    document.title = 'smoke:fail:exception';
}
