import { test as setup } from '@playwright/test';
import { logIn } from './login';
import { sessionFile } from './session';

/** The viewer account (DESK_EMAIL / DESK_PASSWORD) that every chromium test runs as. */
setup('log in', async ({ page }) => {
  if (!process.env.DESK_EMAIL || !process.env.DESK_PASSWORD) throw new Error('Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin).');
  await logIn(page, { email: process.env.DESK_EMAIL, password: process.env.DESK_PASSWORD, stateFile: '.auth/state.json', sessionFile });
});
