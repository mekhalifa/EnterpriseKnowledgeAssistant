import { Routes } from '@angular/router';
import { autoLoginPartialRoutesGuard } from 'angular-auth-oidc-client';
import { Shell } from './shell/shell';

/**
 * Every page is behind sign-in. The guard is a convenience: it starts the sign-in redirect
 * instead of rendering a page that would only collect 401s. It is not a security control —
 * the API authorizes every request on its own (CLAUDE.md: authorization is enforced in retrieval,
 * never only in the UI).
 */
export const routes: Routes = [
  {
    path: 'auth/callback',
    loadComponent: () => import('./core/auth/callback').then((m) => m.Callback),
    title: 'Signing in · EKA',
  },
  {
    path: '',
    component: Shell,
    canActivate: [autoLoginPartialRoutesGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'chat' },
      {
        // One route, the conversation in ?c=. Two routes (chat, chat/:id) would destroy and recreate
        // the page when a first question gives a new conversation its id, cutting off the answer
        // that is streaming at that moment.
        path: 'chat',
        loadComponent: () => import('./features/chat/chat-page').then((m) => m.ChatPage),
        title: 'Ask · EKA',
      },
      {
        path: 'documents',
        loadComponent: () => import('./features/documents/documents-page').then((m) => m.DocumentsPage),
        title: 'Documents · EKA',
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
