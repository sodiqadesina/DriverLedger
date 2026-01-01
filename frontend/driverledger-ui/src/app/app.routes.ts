import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth.guard';

import { LoginComponent } from './features/auth-pages/login/login.component';
import { RegisterComponent } from './features/auth-pages/register/register.component';
import { WelcomeComponent } from './features/auth-pages/welcome/welcome.component';

import { AppShellComponent } from './features/layout/shell/app-shell.component';
import { DashboardComponent } from './features/dashboard/dashboard.component';
import { ReceiptsPage } from './features/receipts/receipts.page';
import { ReceiptReviewPage } from './features/receipts/receipt-review.page';

// placeholders for now (create later)
import { LiveStatementPage } from './features/statements/live-statement.page';
import { LedgerPage } from './features/ledger/ledger.page';
import { LedgerDetailPage } from './features/ledger/ledger-detail.page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'login' },

  { path: 'login', component: LoginComponent },
  { path: 'register', component: RegisterComponent },

  { path: 'welcome', canActivate: [authGuard], component: WelcomeComponent },

  {
    path: 'app',
    canActivate: [authGuard],
    component: AppShellComponent,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', component: DashboardComponent },

      { path: 'receipts', component: ReceiptsPage },
      { path: 'receipts/:id/review', component: ReceiptReviewPage },

      { path: 'ledger', component: LedgerPage },
      { path: 'ledger/:entryId', component: LedgerDetailPage },

      { path: 'live-statement', component: LiveStatementPage },
    ],
  },

  { path: '**', redirectTo: 'login' },
];
