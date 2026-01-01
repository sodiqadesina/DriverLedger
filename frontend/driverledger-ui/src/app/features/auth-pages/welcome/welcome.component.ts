import { Component } from '@angular/core';
import { RouterModule } from '@angular/router';

@Component({
  selector: 'dl-welcome',
  standalone: true,
  imports: [RouterModule],
  template: `
    <div class="min-h-screen flex items-center justify-center p-4">
      <div class="w-full max-w-md border rounded-xl p-6 bg-white">
        <h1 class="text-xl font-semibold">Welcome to DriverLedger</h1>
        <p class="text-sm opacity-70 mt-2">
          This is the onboarding page shown after signup (per design). You can later add steps like:
          connect platform, set default period, upload first receipt.
        </p>

        <div class="mt-6">
          <a class="border rounded p-2 inline-block font-semibold" routerLink="/app/dashboard">
            Continue to Dashboard
          </a>
        </div>
      </div>
    </div>
  `
})
export class WelcomeComponent {}
