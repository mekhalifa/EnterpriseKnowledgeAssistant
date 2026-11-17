import { bootstrapApplication } from '@angular/platform-browser';
import { createAppConfig } from './app/app.config';
import { App } from './app/app';
import { loadRuntimeConfig } from './app/core/config/runtime-config';

// Runtime configuration first (Step 14): the identity provider and API location come from
// /config.json, written at deploy time, so one build serves every environment.
loadRuntimeConfig()
  .then((runtime) => bootstrapApplication(App, createAppConfig(runtime)))
  .catch((err) => console.error(err));
