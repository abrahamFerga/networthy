import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { initTheme } from "@plenipo/ui";
import App from "./App";
import { installIdentityInterceptor } from "./devIdentity";
import "./index.css";
// TODO(plenipo#112): drop with src/table-scroll-shim.css — see the file header for why the shell's
// own table wrapper makes columns unreachable (issue #152).
import "./table-scroll-shim.css";

initTheme();

// Before render, not inside a component: the shell issues its first API calls while mounting, and
// a request that escapes the interceptor goes out as the bundle's hard-coded dev-user/system_admin.
// TODO(plenipo#71): remove with devIdentity.ts once the platform ships a sign-in seam.
installIdentityInterceptor();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
