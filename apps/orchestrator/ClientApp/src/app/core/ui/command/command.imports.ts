import { ZardCommandDividerComponent } from '@ui/command/command-divider.component';
import { ZardCommandEmptyComponent } from '@ui/command/command-empty.component';
import { ZardCommandInputComponent } from '@ui/command/command-input.component';
import { ZardCommandListComponent } from '@ui/command/command-list.component';
import { ZardCommandOptionGroupComponent } from '@ui/command/command-option-group.component';
import { ZardCommandOptionComponent } from '@ui/command/command-option.component';
import { ZardCommandComponent } from '@ui/command/command.component';

export const ZardCommandImports = [
  ZardCommandComponent,
  ZardCommandInputComponent,
  ZardCommandListComponent,
  ZardCommandEmptyComponent,
  ZardCommandOptionComponent,
  ZardCommandOptionGroupComponent,
  ZardCommandDividerComponent,
] as const;
