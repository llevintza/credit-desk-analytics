import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './core/auth.service';
import { Login } from './login/login';
import { Placeholder } from './placeholder/placeholder';
import { Shell } from './shell/shell';

export const routes: Routes = [
  { path: 'login', component: Login, title: 'Sign in · Credit Desk Analytics' },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'positions' },
      // AG Grid lives in this lazy chunk: the initial bundle (login, shell) stays small (README §10).
      { path: 'positions', loadComponent: () => import('./positions/positions').then((m) => m.Positions), title: 'Positions · Credit Desk Analytics' },
      { path: 'funds', component: Placeholder, data: { title: 'Fund Performance', phase: 'phase 5' }, title: 'Fund Performance · Credit Desk Analytics' },
      { path: 'insights', component: Placeholder, data: { title: 'Insights Board', phase: 'phase 6' }, title: 'Insights · Credit Desk Analytics' },
      { path: 'deals', component: Placeholder, data: { title: 'Deal Explorer', phase: 'phase 7' }, title: 'Deals · Credit Desk Analytics' },
      { path: 'lab', component: Placeholder, data: { title: 'Performance Lab', phase: 'phase 8' }, title: 'Performance Lab · Credit Desk Analytics' },
      { path: 'usage', component: Placeholder, canActivate: [adminGuard], data: { title: 'Usage', phase: 'phase 9' }, title: 'Usage · Credit Desk Analytics' },
    ],
  },
  { path: '**', redirectTo: '' },
];
