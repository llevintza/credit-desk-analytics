import { test as setup } from '@playwright/test';
import { logIn } from './login';
import { adminSessionFile } from './session';

/** #278: the admin account (DESK_ADMIN_EMAIL / DESK_ADMIN_PASSWORD) that the admin project runs as. */
setup('log in as admin', async ({ page }) => {
  if (!process.env.DESK_ADMIN_EMAIL || !process.env.DESK_ADMIN_PASSWORD)
    throw new Error('Set DESK_ADMIN_EMAIL and DESK_ADMIN_PASSWORD (create an admin with Desk.UserAdmin --role admin).');
  await logIn(page, { email: process.env.DESK_ADMIN_EMAIL, password: process.env.DESK_ADMIN_PASSWORD, stateFile: '.auth/admin-state.json', sessionFile: adminSessionFile });
});
