import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { startUnlessFramed } from './app/core/frame-guard';

// #66: never start inside a frame (the Pages copy can't send frame-ancestors).
startUnlessFramed(window, document, () => bootstrapApplication(App, appConfig)).catch((err) => console.error(err));
